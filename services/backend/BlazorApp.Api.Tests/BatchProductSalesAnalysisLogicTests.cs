using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using System.Text.Json;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class BatchProductSalesAnalysisLogicTests
{
    [Fact]
    public void NormalizeItemNumbers_去重但保留前导零()
    {
        var items = BatchProductSalesAnalysisService.NormalizeItemNumbers([" 0012 ", "0012", "12"]);
        Assert.Equal(["0012", "12"], items);
    }

    [Fact]
    public void NormalizeItemNumbers_最多允许三千个货号并拒绝超出项()
    {
        var items = BatchProductSalesAnalysisService.NormalizeItemNumbers(
            Enumerable.Range(1, 3000).Select(index => $"ITEM-{index}"));

        Assert.Equal(3000, items.Count);
        var exception = Assert.Throws<BatchProductSalesAnalysisValidationException>(() =>
            BatchProductSalesAnalysisService.NormalizeItemNumbers(
                Enumerable.Range(1, 3001).Select(index => $"ITEM-{index}")));
        Assert.Contains("1 到 3000", exception.Message);
    }

    [Fact]
    public async Task 批量读取跨500边界不会遗漏第501和末项且折扣快照按商品合并()
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-product-sales-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        try
        {
            var day = new DateTime(2026, 9, 1);
            const int count = 3000;
            db.CodeFirst.InitTables(typeof(Product), typeof(Store), typeof(SalesStatisticRefreshState),
                typeof(ProductStoreDailySalesStatistic));
            // SQLite 不支持模型中的 nvarchar(max)，这里用等价 TEXT 建立快照夹具。
            await db.Ado.ExecuteCommandAsync("""
                CREATE TABLE BatchProductSalesDiscountRefreshState (
                    Date TEXT PRIMARY KEY, Status TEXT, RuleVersion INTEGER, StatisticsVersion TEXT,
                    SourceVersion TEXT, RequestedAtUtc TEXT, NextAttemptAtUtc TEXT, Attempts INTEGER,
                    LeaseToken TEXT, LeaseUntilUtc TEXT, CompletedAtUtc TEXT, LastCheckedAtUtc TEXT,
                    LastError TEXT, SnapshotCount INTEGER, ReconcileRequested INTEGER
                );
                CREATE TABLE BatchProductSalesDiscountSnapshot (
                    Id TEXT PRIMARY KEY, SnapshotFormat INTEGER, SourceVersion TEXT, ProductCode TEXT,
                    StartDate TEXT, EndDate TEXT, StoreCodesJson TEXT, Status TEXT, Attempts INTEGER,
                    RequestedAtUtc TEXT, NextAttemptAtUtc TEXT, LeaseToken TEXT, LeaseUntilUtc TEXT,
                    CompletedAtUtc TEXT, PayloadJson TEXT
                );
                """);
            var products = Enumerable.Range(1, count).Select(index => new Product
            {
                ProductCode = $"P-{index:D4}", ItemNumber = $"I-{index:D4}", ProductName = $"商品{index}", IsDeleted = false,
            }).ToList();
            var statistics = products.Select((product, index) => new ProductStoreDailySalesStatistic
            {
                // 保留一条非午夜历史值，验证分店总览仍按整日范围读取。
                Date = index == 500 ? day.AddHours(12) : day, ProductCode = product.ProductCode!, BranchCode = "S1", SupplierCode = "SUP",
                TotalQuantity = 1, TotalAmount = 10, UpdateTime = day,
            }).ToList();
            var rows = products.Select(product => new BatchProductSalesAggregateRow
            {
                Date = day, ProductCode = product.ProductCode!, BranchCode = "S1", Quantity = 1,
                RegularQuantity = 0, DiscountQuantity = 1, SalesAmount = 10,
            }).ToList();
            await db.Insertable(products).ExecuteCommandAsync();
            await db.Insertable(new Store { StoreCode = "S1", StoreName = "测试分店", IsDeleted = false }).ExecuteCommandAsync();
            await db.Insertable(new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily, Date = day, Status = SalesStatisticRefreshStatus.Fresh,
                CompletedAtUtc = day.AddHours(1), SourceProductVersion = "stats-v1",
            }).ExecuteCommandAsync();
            await db.Insertable(statistics).ExecuteCommandAsync();
            await db.Insertable(new BatchProductSalesDiscountRefreshState
            {
                Date = day, Status = "Fresh", RuleVersion = 1, StatisticsVersion = "stats-v1", SourceVersion = "discount-v1",
                CompletedAtUtc = day.AddHours(2), RequestedAtUtc = day, NextAttemptAtUtc = day,
            }).ExecuteCommandAsync();
            await db.Insertable(products.Select((product, index) => new BatchProductSalesDiscountSnapshot
            {
                Id = $"snap-{index:D4}", SnapshotFormat = 2, SourceVersion = "discount-v1", ProductCode = product.ProductCode!,
                StartDate = day, EndDate = day, Status = "Fresh", RequestedAtUtc = day, NextAttemptAtUtc = day,
                CompletedAtUtc = day.AddHours(2), StoreCodesJson = "[\"S1\"]",
                PayloadJson = JsonSerializer.Serialize(new[] { rows[index] }),
            }).ToList()).ExecuteCommandAsync();

            var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
            var service = new BatchProductSalesAnalysisService(db, queue.Object,
                NullLogger<BatchProductSalesAnalysisService>.Instance);
            var summary = (await service.QueryAsync(new BatchProductSalesQueryRequestDto
            {
                ItemNumbers = products.Select(product => product.ItemNumber!).ToList(),
                StartDate = day, EndDate = day, StoreCodes = ["S1"],
            }, ["S1"])).Data!;

            Assert.Equal(count, summary.Products.Count);
            Assert.Equal(1m, Assert.Single(summary.Products, product => product.ItemNumber == "I-0501").Quantity);
            Assert.Equal(1m, Assert.Single(summary.Products, product => product.ItemNumber == "I-0502").Quantity);
            Assert.Equal(1m, Assert.Single(summary.Products, product => product.ItemNumber == "I-3000").Quantity);
            var snapshotReader = new BatchProductSalesDiscountSnapshotReader(db);
            var stateBefore = await snapshotReader.CaptureStateAsync([day], default);
            await db.Updateable<BatchProductSalesDiscountRefreshState>()
                .SetColumns(state => state.Status == "Refreshing")
                .Where(state => state.Date == day)
                .ExecuteCommandAsync();
            var stateChanged = await snapshotReader.CaptureStateAsync([day], default);
            Assert.NotEqual(stateBefore.Fingerprint, stateChanged.Fingerprint);
            // 恢复 Fresh，后续折扣总览验证必须使用同一代已提交状态。
            await db.Updateable<BatchProductSalesDiscountRefreshState>()
                .SetColumns(state => state.Status == "Fresh")
                .Where(state => state.Date == day)
                .ExecuteCommandAsync();
            var lockedRequest = new BatchProductSalesBranchOverviewRequestDto
            {
                ProductCodes = products.Select(product => product.ProductCode!).ToList(),
                StartDate = day, EndDate = day, StoreCodes = ["S1"], BranchCode = "S1",
                CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates,
            };
            var branchOverview = (await service.GetBranchOverviewAsync(lockedRequest, ["S1"])).Data!;
            Assert.Equal(count, branchOverview.Products.Count);
            Assert.Equal(1m, Assert.Single(branchOverview.Products, product => product.ProductCode == "P-0501").Metrics.Quantity);
            Assert.Equal(1m, Assert.Single(branchOverview.Products, product => product.ProductCode == "P-3000").Metrics.Quantity);
            Assert.Equal(count, branchOverview.Branch.Metrics.Quantity);
            Assert.Equal(30000m, branchOverview.Branch.Metrics.SalesAmount);

            var discountException = await Assert.ThrowsAsync<BatchProductSalesAnalysisValidationException>(
                () => service.GetDiscountOverviewAsync(lockedRequest, ["S1"]));
            Assert.Contains("最多支持 500", discountException.Message);
            var statisticRows = await new BatchProductSalesStatisticReader(db)
                .ReadAsync(products.Select(product => product.ProductCode!).ToList(), [day], ["S1"], default);
            Assert.Equal(count, statisticRows.Count);
            Assert.Contains(statisticRows, row => row.ProductCode == "P-0501");
            Assert.Contains(statisticRows, row => row.ProductCode == "P-3000");
            var discountRows = await new BatchProductSalesDiscountSnapshotReader(db).ReadManyAsync(
                products.Select(product => product.ProductCode!).ToList(), [day], ["S1"], rows, default);
            Assert.Equal(count, discountRows.Count);
            Assert.Equal("Fresh", discountRows["P-0501"].Status);
            Assert.Equal(1m, Assert.Single(discountRows["P-0501"].Rows).DiscountQuantity);
            Assert.Equal("Fresh", discountRows["P-3000"].Status);

            // 3000 商品导出必须走日统计 SQL 汇总；删除折扣快照后仍应能产出净销量 CSV，
            // 并把折扣分类明确标为未知，证明没有退回逐商品折扣快照路径。
            await db.Deleteable<BatchProductSalesDiscountSnapshot>().ExecuteCommandAsync();
            var csv = await service.ExportDetailCsvAsync(new BatchProductSalesFollowupRequestDto
            {
                ProductCodes = products.Select(product => product.ProductCode!).ToList(),
                StartDate = day, EndDate = day, StoreCodes = ["S1"],
                CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates,
            }, ["S1"]);
            Assert.Contains("折扣分类未知", csv);
            Assert.Contains("3000", csv);
            Assert.Contains("\"测试分店\",3000,0,0,3000,30000", csv);
        }
        finally
        {
            db.Dispose();
            await connection.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ResolveStoreScope_受限用户篡改分店被拒绝()
    {
        Assert.Throws<BatchProductSalesAnalysisForbiddenException>(() =>
            BatchProductSalesAnalysisService.ResolveStoreScope(["S2"], ["S1"]));
    }

    [Fact]
    public void ResolveStoreScope_受限用户省略门店只使用可见分店()
    {
        Assert.Equal(["S1"], BatchProductSalesAnalysisService.ResolveStoreScope(null, ["S1"]));
    }

    [Fact]
    public void ValidateRange_未来布里斯班日期被拒绝()
    {
        var tomorrow = DateTime.UtcNow.Date.AddDays(2);
        Assert.Throws<BatchProductSalesAnalysisValidationException>(() =>
            BatchProductSalesAnalysisService.ValidateRange(new BlazorApp.Shared.DTOs.BatchProductSalesScopeDto
            {
                StartDate = tomorrow,
                EndDate = tomorrow,
            }));
    }

    [Fact]
    public void BuildAggregateMetrics_同日正价折扣退货保持有符号守恒()
    {
        var metrics = BatchProductSalesAnalysisService.BuildAggregateMetrics(
        [
            new BatchProductSalesAggregateRow { RegularQuantity = 3m, SalesAmount = 30m, OriginalPriceMax = 10m },
            new BatchProductSalesAggregateRow { DiscountQuantity = 1m, ReturnQuantity = 1m, SalesAmount = 8m, OriginalPriceMin = 10m, DiscountPriceMin = 8m },
        ]);

        Assert.Equal(4m, metrics.Quantity);
        Assert.Equal(3m, metrics.RegularQuantity);
        Assert.Equal(1m, metrics.DiscountQuantity);
        Assert.Equal(0m, metrics.UnknownQuantity);
        Assert.Equal(1m, metrics.ReturnQuantity);
        Assert.Equal(metrics.Quantity, metrics.RegularQuantity + metrics.DiscountQuantity + metrics.UnknownQuantity);
        Assert.Equal("complete", metrics.DiscountStatus);
        Assert.Equal(8m, metrics.DiscountPriceMin);
        Assert.Equal(10m, metrics.OriginalPriceMax);
    }

    [Fact]
    public void BuildAggregateMetrics_历史折扣证据缺失明确为unknown而非正价()
    {
        var metrics = BatchProductSalesAnalysisService.BuildAggregateMetrics(
        [new BatchProductSalesAggregateRow { UnknownQuantity = 2m, SalesAmount = 15m, UnknownRowCount = 1 }]);

        Assert.Equal(0m, metrics.RegularQuantity);
        Assert.Equal(0m, metrics.DiscountQuantity);
        Assert.Equal(2m, metrics.UnknownQuantity);
        Assert.Equal("unknown", metrics.DiscountStatus);
        Assert.Null(metrics.OriginalPriceMin);
        Assert.Null(metrics.DiscountPriceMax);
    }

    [Theory]
    [InlineData(0, "unknown")]
    [InlineData(1, "partial")]
    public void BuildAggregateMetrics_未知净量抵消仍不伪装complete(int knownRows, string expectedStatus)
    {
        var facts = new List<BatchProductSalesAggregateRow>
        {
            new() { UnknownQuantity = 1m, SalesAmount = 10m, UnknownRowCount = 1 },
        };
        if (knownRows == 1)
            facts.Add(new() { DiscountQuantity = 1m, SalesAmount = 8m });

        Assert.Equal(expectedStatus, BatchProductSalesAnalysisService.BuildAggregateMetrics(facts).DiscountStatus);
    }

    [Fact]
    public void SplitStatisticQueueSegments_2025走年度队列其他年份保持31天上限且不含Fresh日期()
    {
        var regularDates = Enumerable.Range(0, 32).Select(offset => new DateTime(2024, 11, 1).AddDays(offset));
        var segments = BatchProductSalesAnalysisService.SplitStatisticQueueSegments(
            regularDates.Concat([new DateTime(2025, 1, 2), new DateTime(2025, 1, 3)]));

        Assert.Equal(3, segments.Count);
        Assert.Equal([31, 1, 2], segments.Select(segment => segment.Dates.Count));
        Assert.Equal([false, false, true], segments.Select(segment => segment.IsYearBackfill));
        Assert.DoesNotContain(segments.SelectMany(segment => segment.Dates), date => date == new DateTime(2025, 1, 1));
    }

    [Fact]
    public async Task QueryAsync_缺失和非Fresh日期按分段提交持久队列且不重建Fresh日期()
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-product-sales-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        try
        {
            db.CodeFirst.InitTables(typeof(Product), typeof(Store), typeof(SalesStatisticRefreshState), typeof(ProductStoreDailySalesStatistic));
            await db.Insertable(new Product { ProductCode = "P1", ItemNumber = "001", ProductName = "测试商品", IsDeleted = false }).ExecuteCommandAsync();
            await db.Insertable(new Store { StoreCode = "S1", StoreName = "测试分店", IsDeleted = false }).ExecuteCommandAsync();
            await db.Insertable(new[]
            {
                new SalesStatisticRefreshState { StatisticType = SalesStatisticType.ProductStoreDaily, Date = new DateTime(2024, 12, 31), Status = SalesStatisticRefreshStatus.Fresh },
                new SalesStatisticRefreshState { StatisticType = SalesStatisticType.ProductStoreDaily, Date = new DateTime(2025, 1, 1), Status = SalesStatisticRefreshStatus.Pending },
            }).ExecuteCommandAsync();
            var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
            queue.Setup(service => service.EnqueueAsync(
                    It.Is<IEnumerable<DateTime>>(dates => dates.SequenceEqual(new[] { new DateTime(2024, 12, 30) })),
                    "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProductStoreDailyRecalculationSubmitResult
                {
                    SubmittedDates = [new DateTime(2024, 12, 30)],
                    Status = SalesStatisticRefreshStatus.Queued,
                });
            queue.Setup(service => service.EnqueueYearBackfillAsync(
                    It.Is<IEnumerable<DateTime>>(dates => dates.SequenceEqual(new[] { new DateTime(2025, 1, 1) })),
                    "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProductStoreDailyRecalculationSubmitResult
                {
                    SkippedDates = [new DateTime(2025, 1, 1)],
                    Status = SalesStatisticRefreshStatus.Running,
                });
            var service = new BatchProductSalesAnalysisService(db, queue.Object,
                NullLogger<BatchProductSalesAnalysisService>.Instance);

            var response = await service.QueryAsync(new BatchProductSalesQueryRequestDto
            {
                ItemNumbers = ["001"], StartDate = new DateTime(2024, 12, 30), EndDate = new DateTime(2025, 1, 1),
            }, null);

            Assert.Equal(SalesStatisticRefreshStatus.Pending, response.Data!.StatisticStatus);
            var product = Assert.Single(response.Data.Products);
            Assert.Equal(0m, product.Quantity);
            Assert.Equal("partial", response.Data.Coverage.Status);
            Assert.Equal(["2024-12-31"], response.Data.Coverage.ReadyDates);
            Assert.Contains(response.Data.Coverage.PendingDates, row => row.Date == "2024-12-30" && row.Reason == "queued");
            Assert.Contains(response.Data.Coverage.PendingDates, row => row.Date == "2025-01-01" && row.Reason == "active");
            Assert.Contains(response.Data.Warnings, warning => warning.Contains("1 个缺失或未完成统计日期提交", StringComparison.Ordinal));
            Assert.Contains(response.Data.Warnings, warning => warning.Contains("1 个统计日期已有活动队列任务", StringComparison.Ordinal));
            queue.VerifyAll();
        }
        finally
        {
            db.Dispose();
            await connection.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task QueryAsync_队列提交失败明确告知未排队而不伪称已提交()
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-product-sales-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        try
        {
            db.CodeFirst.InitTables(typeof(Product), typeof(Store), typeof(SalesStatisticRefreshState), typeof(ProductStoreDailySalesStatistic));
            var day = new DateTime(2024, 12, 30);
            await db.Insertable(new Product { ProductCode = "P1", ItemNumber = "001", ProductName = "测试商品", IsDeleted = false }).ExecuteCommandAsync();
            await db.Insertable(new Store { StoreCode = "S1", StoreName = "测试分店", IsDeleted = false }).ExecuteCommandAsync();
            var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
            queue.Setup(service => service.EnqueueAsync(
                    It.Is<IEnumerable<DateTime>>(dates => dates.SequenceEqual(new[] { day })),
                    "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("queue unavailable"));
            var service = new BatchProductSalesAnalysisService(db, queue.Object,
                NullLogger<BatchProductSalesAnalysisService>.Instance);

            var response = await service.QueryAsync(new BatchProductSalesQueryRequestDto
            {
                ItemNumbers = ["001"], StartDate = day, EndDate = day,
            }, null);

            Assert.Equal(SalesStatisticRefreshStatus.Pending, response.Data!.StatisticStatus);
            var product = Assert.Single(response.Data.Products);
            Assert.Null(product.Quantity);
            Assert.Equal("pending", response.Data.Coverage.Status);
            Assert.Contains(response.Data.Coverage.PendingDates, row => row.Date == "2024-12-30" && row.Reason == "queueFailed");
            Assert.Contains(response.Data.Warnings, warning => warning.Contains("提交失败，尚未排队", StringComparison.Ordinal));
            Assert.DoesNotContain(response.Data.Warnings, warning => warning.Contains("提交到持久队列", StringComparison.Ordinal));
            queue.VerifyAll();
        }
        finally
        {
            db.Dispose();
            await connection.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
