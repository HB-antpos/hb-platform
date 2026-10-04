using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed partial class BatchProductSalesAnalysisSqlServerIntegrationTests
{
    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_28和30可用_29非Fresh事实不聚合且限定门店()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1); var day30 = day28.AddDays(2);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null), ("P2", "002", null));
        await _catalog!.Insertable(new Store[]
        {
            new() { StoreCode = "S1", StoreName = "一店", IsDeleted = false },
            new() { StoreCode = "S2", StoreName = "二店", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _catalog.Insertable(new[]
        {
            State(day28, "Fresh", "v28"), State(day29, "Running", "v29"), State(day30, "Fresh", "v30"),
        }).ExecuteCommandAsync();
        await _catalog.Insertable(new[]
        {
            Stat(day28, "P1", "S1", 2), Stat(day29, "P1", "S1", 999), Stat(day30, "P1", "S1", -1),
            Stat(day28, "P1", "S2", 100), Stat(day30, "P1", "S2", 100),
        }).ExecuteCommandAsync();

        var response = await CreateAnalysisService([day29]).QueryAsync(new()
        {
            ItemNumbers = ["001", "002"], StartDate = day28, EndDate = day30, StoreCodes = ["S1"],
        }, ["S1"]);

        Assert.Equal("partial", response.Data!.Coverage.Status);
        Assert.Equal(["2026-08-28", "2026-08-30"], response.Data.Coverage.ReadyDates);
        Assert.Contains(response.Data.Coverage.PendingDates, item => item.Date == "2026-08-29" && item.Reason == "active");
        Assert.Equal(1m, Assert.Single(response.Data.Products, item => item.ProductCode == "P1").Quantity);
        Assert.Equal(1m, Assert.Single(response.Data.Products, item => item.ProductCode == "P1").SalesAmount);
        Assert.Equal(0m, Assert.Single(response.Data.Products, item => item.ProductCode == "P2").Quantity);
        Assert.NotNull(response.Data.Overview.Metrics);
        Assert.Equal(1m, response.Data.Overview.Metrics!.Quantity);
        Assert.Equal(1m, response.Data.Overview.Metrics.SalesAmount);
        var branch = Assert.Single(response.Data.Overview.Branches);
        Assert.Equal("S1", branch.BranchCode);
        Assert.Equal(1m, branch.Metrics.Quantity);
        Assert.Equal(1, branch.ContributingProductCount);
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_全pending仍返回商品null且队列失败明确标记()
    {
        var day = new DateTime(2026, 8, 29);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(Stat(day, "P1", "S1", 999)).ExecuteCommandAsync();
        var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
        queue.Setup(service => service.EnqueueAsync(It.IsAny<IEnumerable<DateTime>>(), "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("isolated queue failure"));
        var service = new BatchProductSalesAnalysisService(_catalog, queue.Object, NullLogger<BatchProductSalesAnalysisService>.Instance);

        var response = await service.QueryAsync(new() { ItemNumbers = ["001"], StartDate = day, EndDate = day }, ["S1"]);

        Assert.Equal("pending", response.Data!.Coverage.Status);
        Assert.Null(Assert.Single(response.Data.Products).Quantity);
        Assert.Contains(response.Data.Coverage.PendingDates, item => item.Date == "2026-08-29" && item.Reason == "queueFailed");
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_摘要锁仅约束ready日期_版本变化冲突而新完成日期不冲突()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { State(day28, "Fresh", "v28"), State(day29, "Pending", "v29") }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { Stat(day28, "P1", "S1", 2), Stat(day29, "P1", "S1", 99) }).ExecuteCommandAsync();
        var service = CreateAnalysisService();
        var summary = (await service.QueryAsync(new() { ItemNumbers = ["001"], StartDate = day28, EndDate = day29 }, ["S1"])).Data!;
        var locked = new BatchProductSalesDetailRequestDto
        {
            ProductCode = "P1", StartDate = day28, EndDate = day29,
            CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates,
        };

        // 原 pending 日变 Fresh 不属于摘要锁集合，不得阻断详情，也不得把 99 混入销量。
        await _catalog.Updateable<SalesStatisticRefreshState>().SetColumns(state => state.Status == "Fresh")
            .Where(state => state.Date == day29).ExecuteCommandAsync();
        var detail = (await service.GetDetailAsync(locked, ["S1"])).Data!;
        Assert.Equal(2m, detail.Metrics.Quantity);
        Assert.Equal([day28], detail.Daily.Select(item => item.Date));

        await _catalog.Updateable<SalesStatisticRefreshState>().SetColumns(state => state.SourceProductVersion == "v28-changed")
            .Where(state => state.Date == day28).ExecuteCommandAsync();
        await Assert.ThrowsAsync<BatchProductSalesCoverageVersionConflictException>(() => service.GetDetailAsync(locked, ["S1"]));
    }


    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_队列失败与非Fresh状态不阻断已完成日期()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1); var day30 = day28.AddDays(2);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        // 表的主键保证同一统计类型每天只有一条状态；这里用非 Fresh 状态验证已有事实不会污染可用日期。
        await _catalog.Insertable(new[]
        {
            State(day28, "Fresh", "v28"), State(day30, "Failed", "v30"),
        }).ExecuteCommandAsync();
        await _catalog.Insertable(new[]
        {
            Stat(day28, "P1", "S1", 2), Stat(day29, "P1", "S1", 99), Stat(day30, "P1", "S1", 999),
        }).ExecuteCommandAsync();
        var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
        queue.Setup(service => service.EnqueueAsync(It.IsAny<IEnumerable<DateTime>>(), "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("isolated queue failure"));
        var service = new BatchProductSalesAnalysisService(_catalog, queue.Object, NullLogger<BatchProductSalesAnalysisService>.Instance);

        var response = await service.QueryAsync(new() { ItemNumbers = ["001"], StartDate = day28, EndDate = day30 }, ["S1"]);

        Assert.Equal("partial", response.Data!.Coverage.Status);
        Assert.Equal(["2026-08-28"], response.Data.Coverage.ReadyDates);
        Assert.Equal(2m, Assert.Single(response.Data.Products).Quantity);
        Assert.Contains(response.Data.Coverage.PendingDates, item => item.Date == "2026-08-29" && item.Reason == "queueFailed");
        Assert.Contains(response.Data.Coverage.PendingDates, item => item.Date == "2026-08-30" && item.Reason == "queueFailed");
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_读取期间pending转Fresh仍从摘要剔除且不回报过时队列状态()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { State(day28, "Fresh", "v28"), State(day29, "Pending", "v29") }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { Stat(day28, "P1", "S1", 2), Stat(day29, "P1", "S1", 3) }).ExecuteCommandAsync();
        var changed = false;
        _catalog.Aop.OnLogExecuted = (sql, _) =>
        {
            if (changed || !sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)) return;
            changed = true;
            using var writer = Client(WithDatabase(_master!, CatalogName));
            writer.Updateable<SalesStatisticRefreshState>().SetColumns(state => state.Status == "Fresh")
                .Where(state => state.Date == day29).ExecuteCommand();
        };
        try
        {
            var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
            queue.Setup(service => service.EnqueueAsync(It.IsAny<IEnumerable<DateTime>>(), "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("queue failed before another worker completes the date"));
            var service = new BatchProductSalesAnalysisService(_catalog, queue.Object, NullLogger<BatchProductSalesAnalysisService>.Instance);
            var response = await service.QueryAsync(new() { ItemNumbers = ["001"], StartDate = day28, EndDate = day29 }, ["S1"]);

            Assert.True(changed);
            Assert.Equal("partial", response.Data!.Coverage.Status);
            Assert.Equal(["2026-08-28"], response.Data.Coverage.ReadyDates);
            Assert.Equal(2m, Assert.Single(response.Data.Products).Quantity);
            Assert.Contains(response.Data.Coverage.PendingDates, item => item.Date == "2026-08-29" && item.Reason == "changed");
        }
        finally
        {
            _catalog.Aop.OnLogExecuted = null;
        }
    }


    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_读取期间原ready版本变化从摘要剔除()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { State(day28, "Fresh", "v28"), State(day29, "Fresh", "v29") }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { Stat(day28, "P1", "S1", 2), Stat(day29, "P1", "S1", 3) }).ExecuteCommandAsync();
        var changed = false;
        _catalog.Aop.OnLogExecuted = (sql, _) =>
        {
            if (changed || !sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)) return;
            changed = true;
            using var writer = Client(WithDatabase(_master!, CatalogName));
            writer.Updateable<SalesStatisticRefreshState>().SetColumns(state => state.SourceProductVersion == "v29-changed")
                .Where(state => state.Date == day29).ExecuteCommand();
        };
        try
        {
            var response = await CreateAnalysisService().QueryAsync(new() { ItemNumbers = ["001"], StartDate = day28, EndDate = day29 }, ["S1"]);

            Assert.True(changed);
            Assert.Equal("partial", response.Data!.Coverage.Status);
            Assert.Equal(["2026-08-28"], response.Data.Coverage.ReadyDates);
            Assert.Equal(2m, Assert.Single(response.Data.Products).Quantity);
            Assert.Contains(response.Data.Coverage.PendingDates, item => item.Date == "2026-08-29" && item.Reason == "changed");
        }
        finally
        {
            _catalog.Aop.OnLogExecuted = null;
        }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export按日分店累计并保留CSV区段()
    {
        var day = new DateTime(2026, 8, 28);
        await PrepareAnalysisSchemaAsync(); await SeedProductsAsync(("P1", "001", null), ("P2", "002", null));
        await _catalog!.Insertable(new[]
        {
            new Store { StoreCode = "S1", StoreName = " =HYPERLINK(\"bad\")", IsDeleted = false },
            new Store { StoreCode = "S2", StoreName = "二店", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _catalog.Insertable(State(day, "Fresh", "v1")).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { Stat(day, "P1", "S1", 2), Stat(day, "P2", "S1", 3) }).ExecuteCommandAsync();
        await _catalog.Insertable(new BatchProductSalesDiscountRefreshState { Date = day, Status = "Fresh", RuleVersion = 1, SourceVersion = "src", StatisticsVersion = "stat", CompletedAtUtc = day.AddHours(2) }).ExecuteCommandAsync();
        foreach (var pair in new[] { ("P1", 2), ("P2", 3) })
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new List<BatchProductSalesAggregateRow> { new() { Date = day, BranchCode = "S1", ProductCode = pair.Item1, Quantity = pair.Item2, RegularQuantity = pair.Item2, SalesAmount = pair.Item2 } });
            await _catalog.Insertable(new BatchProductSalesDiscountSnapshot { Id = Guid.NewGuid().ToString("N"), SnapshotFormat = 2, SourceVersion = "src", ProductCode = pair.Item1, StartDate = day, EndDate = day, Status = "Fresh", CompletedAtUtc = day.AddHours(2), PayloadJson = payload }).ExecuteCommandAsync();
        }
        var summary = (await CreateAnalysisService().QueryAsync(new() { ItemNumbers = ["001", "002"], StartDate = day, EndDate = day, StoreCodes = ["S1", "S2"] }, ["S1", "S2"])).Data!;
        var csv = await CreateAnalysisService().ExportDetailCsvAsync(new() { ProductCodes = ["P1", "P2"], StartDate = day, EndDate = day, StoreCodes = ["S1", "S2"], CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates }, ["S1", "S2"]);
        Assert.Contains("Date,Quantity,Regular,Discount,Unknown,Amount,Status", csv);
        Assert.Contains("Branch,Quantity,Regular,Discount,Unknown,Amount", csv);
        Assert.Contains("Branch,Date,Quantity,Regular,Discount,Unknown,Amount,Status", csv);
        Assert.Contains("2026-08-28,5,5,0,0,5,complete", csv);
        var storeRange = csv.Split(Environment.NewLine).Single(line => line.StartsWith("门店范围,", StringComparison.Ordinal));
        Assert.StartsWith("门店范围,\"", storeRange, StringComparison.Ordinal);
        Assert.EndsWith("\"", storeRange, StringComparison.Ordinal);
        Assert.Equal(["S1", "S2"], storeRange["门店范围,\"".Length..^1]
            .Split('|').OrderBy(code => code, StringComparer.Ordinal).ToArray());
        Assert.Contains("\"' =HYPERLINK(\"\"bad\"\")\"", csv);
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export完整日期轴将未完成日留空且排除脏事实()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1); var day30 = day29.AddDays(1);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(new[]
        {
            State(day28, "Fresh", "v28"), State(day29, "Running", "v29"), State(day30, "Fresh", "v30"),
        }).ExecuteCommandAsync();
        // 29 日的巨大事实是未完成统计，导出只能保留日期轴，绝不能把它聚合进任何销量列。
        await _catalog.Insertable(new[] { Stat(day29, "P1", "S1", 999), Stat(day30, "P1", "S1", 2) }).ExecuteCommandAsync();
        await _catalog.Insertable(new[]
        {
            new BatchProductSalesDiscountRefreshState { Date = day28, Status = "Fresh", RuleVersion = 1, SourceVersion = "src", StatisticsVersion = "stat", CompletedAtUtc = day28.AddHours(2) },
            new BatchProductSalesDiscountRefreshState { Date = day30, Status = "Fresh", RuleVersion = 1, SourceVersion = "src", StatisticsVersion = "stat", CompletedAtUtc = day30.AddHours(2) },
        }).ExecuteCommandAsync();
        var payload = System.Text.Json.JsonSerializer.Serialize(new List<BatchProductSalesAggregateRow>
        {
            new() { Date = day30, BranchCode = "S1", ProductCode = "P1", Quantity = 2, RegularQuantity = 2, SalesAmount = 2 },
        });
        await _catalog.Insertable(new BatchProductSalesDiscountSnapshot { Id = Guid.NewGuid().ToString("N"), SnapshotFormat = 2, SourceVersion = "src", ProductCode = "P1", StartDate = day30, EndDate = day30, Status = "Fresh", CompletedAtUtc = day30.AddHours(2), PayloadJson = payload }).ExecuteCommandAsync();

        var service = CreateAnalysisService();
        var summary = (await service.QueryAsync(new() { ItemNumbers = ["001"], StartDate = day28, EndDate = day30, StoreCodes = ["S1"] }, ["S1"])).Data!;
        var csv = await service.ExportDetailCsvAsync(new()
        {
            ProductCodes = ["P1"], StartDate = day28, EndDate = day30, StoreCodes = ["S1"],
            CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates,
        }, ["S1"]);

        Assert.Contains("销量口径,\"仅含已完成日期；未统计日期数值为空且未计入合计\"", csv);
        Assert.Contains("商品范围,\"P1\"", csv);
        Assert.Contains("2026-08-28,0,0,0,0,0,complete", csv); // 已完成的真实零日仍为零。
        var pendingDaily = ParseCsvLine(csv.Split(Environment.NewLine).Single(line => line.StartsWith("2026-08-29,", StringComparison.Ordinal)));
        Assert.Equal(["2026-08-29", "", "", "", "", "", "pending"], pendingDaily);
        Assert.Contains("2026-08-30,2,2,0,0,2,complete", csv);
        Assert.DoesNotContain("999", csv);
        var lines = csv.Split(Environment.NewLine);
        var branchDailyRows = lines.Skip(Array.IndexOf(lines, "分店每日") + 2).ToList();
        var pendingBranch = ParseCsvLine(branchDailyRows.Single(line => line.Contains(",2026-08-29,", StringComparison.Ordinal)));
        Assert.Equal(8, pendingBranch.Count);
        Assert.Equal("2026-08-29", pendingBranch[1]);
        Assert.Equal(["", "", "", "", "", "pending"], pendingBranch.Skip(2));
        var zeroBranch = ParseCsvLine(branchDailyRows.Single(line => line.Contains(",2026-08-28,", StringComparison.Ordinal)));
        Assert.Equal(["2026-08-28", "0", "0", "0", "0", "0", "complete"], zeroBranch.Skip(1));
        var completeBranch = ParseCsvLine(branchDailyRows.Single(line => line.Contains(",2026-08-30,", StringComparison.Ordinal)));
        Assert.Equal(["2026-08-30", "2", "2", "0", "0", "2", "complete"], completeBranch.Skip(1));
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append(character);
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else value.Append(character);
        }
        fields.Add(value.ToString());
        return fields;
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export最终锁变化409且清理本次临时文件()
    {
        var setup = await PrepareLockedExportAsync();
        var before = ExportTemporaryFiles(); var stateReads = 0;
        // 第三次是文件落盘后的最终 CoverageAsync；必须在该 SELECT 前改变版本，
        // OnLogExecuted 会晚到读取结果已物化之后，无法验证最终锁门。
        _catalog!.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.Contains("SalesStatisticRefreshState", StringComparison.OrdinalIgnoreCase)) return;
            if (++stateReads != 3) return;
            using var writer = Client(WithDatabase(_master!, CatalogName));
            writer.Updateable<SalesStatisticRefreshState>().SetColumns(x => x.SourceProductVersion == "changed-after-file")
                .Where(x => x.Date == setup.Day).ExecuteCommand();
        };
        try
        {
            await Assert.ThrowsAsync<BatchProductSalesCoverageVersionConflictException>(() => setup.Service.ExportDetailCsvAsync(setup.Request, ["S1"]));
            Assert.Equal(before, ExportTemporaryFiles());
            Assert.Equal(3, stateReads);
        }
        finally { _catalog.Aop.OnLogExecuting = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export最终读取取消且清理本次临时文件()
    {
        var setup = await PrepareLockedExportAsync();
        var before = ExportTemporaryFiles(); var stateReads = 0; using var cts = new CancellationTokenSource();
        _catalog!.Aop.OnLogExecuted = (sql, _) =>
        {
            if (!sql.Contains("SalesStatisticRefreshState", StringComparison.OrdinalIgnoreCase)) return;
            if (++stateReads == 3) cts.Cancel();
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.ExportDetailCsvAsync(setup.Request, ["S1"], cts.Token));
            Assert.Equal(before, ExportTemporaryFiles());
            Assert.Equal(3, stateReads);
        }
        finally { _catalog.Aop.OnLogExecuted = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export快照死锁重试两次后成功()
    {
        var setup = await PrepareLockedExportAsync();
        var attempts = 0;
        _catalog!.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsDiscountSnapshotSelect(sql) && ++attempts <= 2)
                throw CreateSqlException(1205);
        };
        try
        {
            var csv = await setup.Service.ExportDetailCsvAsync(setup.Request, ["S1"]);
            Assert.Equal(3, attempts);
            Assert.Contains("2026-08-28,2,2,0,0,2,complete", csv);
        }
        finally { _catalog.Aop.OnLogExecuting = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export快照死锁耗尽重试仍原样失败()
    {
        var setup = await PrepareLockedExportAsync();
        var attempts = 0;
        _catalog!.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsDiscountSnapshotSelect(sql))
            {
                attempts++;
                throw CreateSqlException(1205);
            }
        };
        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => setup.Service.ExportDetailCsvAsync(setup.Request, ["S1"]));
            Assert.Equal(1205, exception.Number);
            Assert.Equal(3, attempts);
        }
        finally { _catalog.Aop.OnLogExecuting = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export非死锁错误不重试()
    {
        var setup = await PrepareLockedExportAsync();
        var attempts = 0;
        _catalog!.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsDiscountSnapshotSelect(sql))
            {
                attempts++;
                throw CreateSqlException(50000);
            }
        };
        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => setup.Service.ExportDetailCsvAsync(setup.Request, ["S1"]));
            Assert.Equal(50000, exception.Number);
            Assert.Equal(1, attempts);
        }
        finally { _catalog.Aop.OnLogExecuting = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_export死锁退避可取消()
    {
        var setup = await PrepareLockedExportAsync();
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        _catalog!.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsDiscountSnapshotSelect(sql))
            {
                attempts++;
                // 已进入快照 SELECT 并命中死锁后才取消，确保取消发生在 export 的退避等待中。
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                throw CreateSqlException(1205);
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.ExportDetailCsvAsync(setup.Request, ["S1"], cancellation.Token));
            Assert.Equal(1, attempts);
        }
        finally { _catalog.Aop.OnLogExecuting = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_detail不含折扣不读取快照且销量分类保持未知()
    {
        var setup = await PrepareLockedExportAsync();
        var snapshotReads = 0;
        _catalog!.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsDiscountSnapshotSelect(sql)) snapshotReads++;
        };
        try
        {
            var detail = (await setup.Service.GetDetailAsync(new()
            {
                ProductCode = "P1", StartDate = setup.Day, EndDate = setup.Day, StoreCodes = ["S1"],
                CoverageVersion = setup.Request.CoverageVersion, ReadyDates = setup.Request.ReadyDates,
                IncludeDiscounts = false,
            }, ["S1"])).Data!;

            Assert.Equal("Pending", detail.DiscountStatisticStatus);
            Assert.Equal(2m, detail.Metrics.Quantity);
            Assert.Equal(2m, detail.Metrics.SalesAmount);
            Assert.Equal("unknown", detail.Metrics.DiscountStatus);
            Assert.Equal("unknown", Assert.Single(detail.Daily).Metrics.DiscountStatus);
            Assert.Equal("unknown", Assert.Single(detail.Branches).Metrics.DiscountStatus);
            Assert.Equal(0, snapshotReads);
        }
        finally { _catalog.Aop.OnLogExecuting = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_C2稳定日期再次变化返回409语义异常()
    {
        var day28 = new DateTime(2026, 8, 28); var day29 = day28.AddDays(1);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { State(day28, "Fresh", "v28"), State(day29, "Fresh", "v29") }).ExecuteCommandAsync();
        await _catalog.Insertable(new[] { Stat(day28, "P1", "S1", 2), Stat(day29, "P1", "S1", 3) }).ExecuteCommandAsync();
        // 按阶段注入而非按分组查询序号：摘要聚合拆成几条 SQL 都不影响注入时机。
        // C1 前改 day29 → C1 只保留 day28 为稳定日期；C1 之后的稳定重读期间改 day28 → C2 必须报冲突。
        var grouped = 0;
        var coverageReads = 0;
        var c2Injected = false;
        _catalog.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.Contains("SalesStatisticRefreshState", StringComparison.OrdinalIgnoreCase)
                && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                coverageReads++;
                return;
            }
            if (!sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase) || !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)) return;
            grouped++;
            using var writer = Client(WithDatabase(_master!, CatalogName));
            if (grouped == 1)
                writer.Updateable<SalesStatisticRefreshState>().SetColumns(x => x.SourceProductVersion == "v29-c1").Where(x => x.Date == day29).ExecuteCommand();
            else if (coverageReads >= 2 && !c2Injected)
            {
                c2Injected = true;
                writer.Updateable<SalesStatisticRefreshState>().SetColumns(x => x.SourceProductVersion == "v28-c2").Where(x => x.Date == day28).ExecuteCommand();
            }
        };
        try
        {
            await Assert.ThrowsAsync<BatchProductSalesCoverageVersionConflictException>(() => CreateAnalysisService().QueryAsync(new() { ItemNumbers = ["001"], StartDate = day28, EndDate = day29 }, ["S1"]));
            Assert.True(c2Injected, "C1 之后的稳定日期重读必须发生，才能验证 C2 冲突");
        }
        finally { _catalog.Aop.OnLogExecuted = null; }
    }

    [BatchSalesSqlServerFact]
    public async Task PartialCoverage_SQLServer_followup请求越权门店在Reader前拒绝()
    {
        var day = new DateTime(2026, 8, 28);
        await PrepareAnalysisSchemaAsync();
        await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new[] { new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }, new Store { StoreCode = "S2", StoreName = "二店", IsDeleted = false } }).ExecuteCommandAsync();
        await _catalog.Insertable(State(day, "Fresh", "v1")).ExecuteCommandAsync();
        var summary = (await CreateAnalysisService().QueryAsync(new() { ItemNumbers = ["001"], StartDate = day, EndDate = day, StoreCodes = ["S1"] }, ["S1"])).Data!;
        await Assert.ThrowsAsync<BatchProductSalesAnalysisForbiddenException>(() => CreateAnalysisService().GetBranchOverviewAsync(new()
        { ProductCodes = ["P1"], BranchCode = "S2", StartDate = day, EndDate = day, StoreCodes = ["S2"], CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates }, ["S1"]));
    }

    private async Task<(DateTime Day, BatchProductSalesAnalysisService Service, BatchProductSalesFollowupRequestDto Request)> PrepareLockedExportAsync()
    {
        var day = new DateTime(2026, 8, 28);
        await PrepareAnalysisSchemaAsync(); await SeedProductsAsync(("P1", "001", null));
        await _catalog!.Insertable(new Store { StoreCode = "S1", StoreName = "一店", IsDeleted = false }).ExecuteCommandAsync();
        await _catalog.Insertable(State(day, "Fresh", "v1")).ExecuteCommandAsync();
        await _catalog.Insertable(Stat(day, "P1", "S1", 2)).ExecuteCommandAsync();
        await _catalog.Insertable(new BatchProductSalesDiscountRefreshState { Date = day, Status = "Fresh", RuleVersion = 1, SourceVersion = "src", StatisticsVersion = "stat", CompletedAtUtc = day.AddHours(2) }).ExecuteCommandAsync();
        var payload = System.Text.Json.JsonSerializer.Serialize(new List<BatchProductSalesAggregateRow> { new() { Date = day, BranchCode = "S1", ProductCode = "P1", Quantity = 2, RegularQuantity = 2, SalesAmount = 2 } });
        await _catalog.Insertable(new BatchProductSalesDiscountSnapshot { Id = Guid.NewGuid().ToString("N"), SnapshotFormat = 2, SourceVersion = "src", ProductCode = "P1", StartDate = day, EndDate = day, Status = "Fresh", CompletedAtUtc = day.AddHours(2), PayloadJson = payload }).ExecuteCommandAsync();
        var service = CreateAnalysisService();
        var summary = (await service.QueryAsync(new() { ItemNumbers = ["001"], StartDate = day, EndDate = day, StoreCodes = ["S1"] }, ["S1"])).Data!;
        return (day, service, new() { ProductCodes = ["P1"], StartDate = day, EndDate = day, StoreCodes = ["S1"], CoverageVersion = summary.Coverage.Version, ReadyDates = summary.Coverage.ReadyDates });
    }

    private static List<string> ExportTemporaryFiles() => Directory.GetFiles(Path.GetTempPath(), "batch-product-sales-*.csv").OrderBy(path => path, StringComparer.Ordinal).ToList();

    private static bool IsDiscountSnapshotSelect(string sql) =>
        sql.Contains("FROM [BatchProductSalesDiscountSnapshot]", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("SnapshotFormat", StringComparison.OrdinalIgnoreCase);

    private static SqlException CreateSqlException(int number)
    {
        var error = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError).GetField("_number", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(error, number);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errors, [error]);
        var exception = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(SqlException).GetField("_errors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(exception, errors);
        return exception;
    }

    private async Task PrepareAnalysisSchemaAsync()
    {
        _catalog!.CodeFirst.InitTables<Product, Store, SalesStatisticRefreshState, ProductStoreDailySalesStatistic>();
        _catalog.CodeFirst.InitTables<BatchProductSalesDiscountRefreshState, BatchProductSalesDiscountSnapshot>();
        await Task.CompletedTask;
    }

    private BatchProductSalesAnalysisService CreateAnalysisService(IReadOnlyList<DateTime>? skippedDates = null)
    {
        skippedDates ??= [];
        var queue = new Mock<IProductStoreDailyStatisticQueueService>();
        queue.Setup(service => service.EnqueueAsync(It.IsAny<IEnumerable<DateTime>>(), "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductStoreDailyRecalculationSubmitResult { SkippedDates = skippedDates.ToList() });
        queue.Setup(service => service.EnqueueYearBackfillAsync(It.IsAny<IEnumerable<DateTime>>(), "batch-product-sales-analysis", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductStoreDailyRecalculationSubmitResult { SkippedDates = skippedDates.ToList() });
        return new BatchProductSalesAnalysisService(_catalog!, queue.Object, NullLogger<BatchProductSalesAnalysisService>.Instance);
    }

    private static SalesStatisticRefreshState State(DateTime day, string status, string version) => new()
    {
        StatisticType = SalesStatisticType.ProductStoreDaily, Date = day, Status = status,
        CompletedAtUtc = status == "Fresh" ? day.AddHours(1) : null, SourceProductVersion = version,
    };

    private static ProductStoreDailySalesStatistic Stat(DateTime day, string product, string store, int quantity) => new()
    {
        Date = day, ProductCode = product, BranchCode = store, SupplierCode = "SUP", TotalQuantity = quantity, TotalAmount = quantity,
    };
}
