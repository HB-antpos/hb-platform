using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>商品分店每日统计 writer 的纯回归测试，不依赖数据库或外部来源。</summary>
public sealed class SalesStatisticsProductStoreDailyCommandWriterTests
{
    private static readonly DateTime RetryTargetDate = new(2026, 9, 20);

    [Fact]
    public async Task 成本锁重试_首次成功不等待也不重试()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var expected = SuccessfulPersistResult();

        var actual = await SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
            persistOnceAsync: () =>
            {
                attempts++;
                return Task.FromResult(expected);
            },
            canRetryAfterRollback: () => true,
            targetDate: RetryTargetDate,
            logger: NullLogger.Instance,
            delayAsync: delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task 成本锁重试_短暂冲突后成功且按退避表依次等待()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var expected = SuccessfulPersistResult();

        var actual = await SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
            persistOnceAsync: () =>
            {
                attempts++;
                if (attempts < 3)
                    throw LockException(-1);
                return Task.FromResult(expected);
            },
            canRetryAfterRollback: () => true,
            targetDate: RetryTargetDate,
            logger: NullLogger.Instance,
            delayAsync: delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Same(expected, actual);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, delays);
    }

    [Fact]
    public async Task 成本锁重试_退避表用尽仍冲突时原样抛出最后异常()
    {
        var maxAttempts = SalesStatisticsProductStoreDailyCommandWriter.LockRetryDelays.Length + 1;
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var finalError = LockException(-1);

        var actual = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
            SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
                persistOnceAsync: () =>
                {
                    attempts++;
                    throw attempts == maxAttempts ? finalError : LockException(-1);
                },
                canRetryAfterRollback: () => true,
                targetDate: RetryTargetDate,
                logger: NullLogger.Instance,
                delayAsync: delay =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                }));

        Assert.Same(finalError, actual);
        Assert.Equal(7, attempts);
        // 7 次尝试各等 10 秒 + 退避合计 202 秒，覆盖另一笔日统计约 4.5 分钟的锁持有。
        Assert.Equal(
            new[] { 2, 5, 15, 30, 60, 90 }.Select(seconds => TimeSpan.FromSeconds(seconds)),
            delays);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public async Task 成本锁重试_非等待超时结果码不重试(int resultCode)
    {
        var attempts = 0;
        var expected = LockException(resultCode);

        var actual = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
            SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
                persistOnceAsync: () =>
                {
                    attempts++;
                    throw expected;
                },
                canRetryAfterRollback: () => true,
                targetDate: RetryTargetDate,
                logger: NullLogger.Instance,
                delayAsync: _ => throw new Xunit.Sdk.XunitException("非等待超时不可退避")));

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task 成本锁重试_一般异常和回滚未确认均不重试()
    {
        var ordinary = new InvalidOperationException("write failed");
        var ordinaryAttempts = 0;
        var ordinaryActual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
                persistOnceAsync: () =>
                {
                    ordinaryAttempts++;
                    throw ordinary;
                },
                canRetryAfterRollback: () => true,
                targetDate: RetryTargetDate,
                logger: NullLogger.Instance,
                delayAsync: _ => throw new Xunit.Sdk.XunitException("一般异常不可退避")));
        Assert.Same(ordinary, ordinaryActual);
        Assert.Equal(1, ordinaryAttempts);

        var lockError = LockException(-1);
        var lockAttempts = 0;
        var lockActual = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
            SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
                persistOnceAsync: () =>
                {
                    lockAttempts++;
                    throw lockError;
                },
                canRetryAfterRollback: () => false,
                targetDate: RetryTargetDate,
                logger: NullLogger.Instance,
                delayAsync: _ => throw new Xunit.Sdk.XunitException("回滚未确认不可退避")));
        Assert.Same(lockError, lockActual);
        Assert.Equal(1, lockAttempts);
    }

    [Fact]
    public async Task 成本锁重试_真实事务执行器先回滚再开始第二次尝试()
    {
        var steps = new List<string>();
        var attempts = 0;
        var transactionOpen = false;
        var rollbackConfirmed = false;
        var expected = SuccessfulPersistResult();

        var actual = await SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
            persistOnceAsync: async () =>
            {
                attempts++;
                var currentAttempt = attempts;
                await SalesStatisticsTransactionExecutor.ExecuteAsync(
                    beginAsync: () =>
                    {
                        Assert.False(transactionOpen);
                        transactionOpen = true;
                        rollbackConfirmed = false;
                        steps.Add($"begin-{currentAttempt}");
                        return Task.CompletedTask;
                    },
                    workAsync: () =>
                    {
                        steps.Add($"work-{currentAttempt}");
                        if (currentAttempt == 1)
                            throw LockException(-1);
                        return Task.CompletedTask;
                    },
                    commitAsync: () =>
                    {
                        steps.Add($"commit-{currentAttempt}");
                        transactionOpen = false;
                        return Task.CompletedTask;
                    },
                    rollbackAsync: () =>
                    {
                        steps.Add($"rollback-{currentAttempt}");
                        transactionOpen = false;
                        rollbackConfirmed = true;
                        return Task.CompletedTask;
                    },
                    logger: NullLogger.Instance,
                    operationName: "测试成本锁事务");
                return expected;
            },
            canRetryAfterRollback: () => rollbackConfirmed && !transactionOpen,
            targetDate: RetryTargetDate,
            logger: NullLogger.Instance,
            delayAsync: delay =>
            {
                steps.Add($"delay-{delay.TotalSeconds:0}");
                return Task.CompletedTask;
            });

        Assert.Same(expected, actual);
        Assert.Equal(new[] { "begin-1", "work-1", "rollback-1", "delay-2", "begin-2", "work-2", "commit-2" }, steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 成本锁重试_事务回滚失败无论连接事务是否清空都不重试(bool transactionStaysOpen)
    {
        var steps = new List<string>();
        var transactionOpen = false;
        var rollbackConfirmed = false;
        var canRetryCalls = 0;
        var original = LockException(-1);
        var actual = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
            SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
                persistOnceAsync: async () =>
                {
                    await SalesStatisticsTransactionExecutor.ExecuteAsync(
                        beginAsync: () =>
                        {
                            transactionOpen = true;
                            steps.Add("begin");
                            return Task.CompletedTask;
                        },
                        workAsync: () =>
                        {
                            steps.Add("work");
                            throw original;
                        },
                        commitAsync: () => throw new Xunit.Sdk.XunitException("失败事务不可提交"),
                        rollbackAsync: () =>
                        {
                            steps.Add("rollback-failed");
                            transactionOpen = transactionStaysOpen;
                            throw new InvalidOperationException("rollback failed");
                        },
                        logger: NullLogger.Instance,
                        operationName: "测试回滚失败");
                    return SuccessfulPersistResult();
                },
                canRetryAfterRollback: () =>
                {
                    canRetryCalls++;
                    return rollbackConfirmed && !transactionOpen;
                },
                targetDate: RetryTargetDate,
                logger: NullLogger.Instance,
                delayAsync: _ => throw new Xunit.Sdk.XunitException("回滚失败不可退避")));

        Assert.Same(original, actual);
        Assert.False(rollbackConfirmed);
        Assert.Equal(transactionStaysOpen, transactionOpen);
        Assert.Equal(1, canRetryCalls);
        Assert.Equal(new[] { "begin", "work", "rollback-failed" }, steps);
    }

    private static SetChildPurchasePriceLockException LockException(int resultCode) =>
        new("HB:SetChildPurchasePrice:Product:TEST", resultCode);

    private static SalesStatisticsProductStoreDailyCommandWriter.PersistResult SuccessfulPersistResult() =>
        new(new SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult("Fresh", null), null);

    [Fact]
    public void 历史快照已完整或可按旧单价派生时不锁当前商品成本()
    {
        var old = new[]
        {
            Statistic("P1", 2, 20m, 3m, 6m, "ProductPurchasePrice"),
            Statistic("P2", 2, 20m, 4m, null, "StoreRetailPrice"),
            Statistic("ZERO", 1, 0m, 0m, 0m, "ProductPurchasePrice"),
            Statistic("DELETED", 1, 10m, 2m, 2m, "ProductPurchasePrice"),
        };
        var rebuilt = new[]
        {
            Statistic("P1", 2, 20m, 99m, 198m, "ProductPurchasePrice"),
            Statistic("P2", 3, 30m, 99m, 297m, "ProductPurchasePrice"),
            Statistic("ZERO", 1, 0m, 99m, 99m, "ProductPurchasePrice"),
            Statistic("OPENITEM", 2, 20m, 8m, 16m, "OpenItem"),
        };

        var codes = SalesStatisticsProductStoreDailyCommandWriter.ResolveCurrentCostProductCodes(
            old[0].Date, ["P1", "P2", "ZERO", "DELETED", "OPENITEM"], rebuilt, old);

        Assert.Equal(new[] { "OPENITEM" }, codes);
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(rebuilt, old);
        Assert.Equal(new decimal?[] { 6m, 12m, 0m, 16m }, rebuilt.Select(row => row.TotalCost));
    }

    [Fact]
    public void 历史新增缺失及无旧单价但事实改变的商品仍锁当前成本()
    {
        var old = new[]
        {
            Statistic("MISSING", 2, 20m, null, null, "Missing"),
            Statistic("CHANGED", 2, 20m, null, 6m, "ProductPurchasePrice"),
            Statistic("SHARED", 2, 20m, 3m, 6m, "ProductPurchasePrice"),
        };
        var sharedNewStore = Statistic("SHARED", 1, 10m, 8m, 8m, "StoreRetailPrice");
        sharedNewStore.BranchCode = "NEW";
        var rebuilt = new[]
        {
            Statistic("MISSING", 2, 20m, 8m, 16m, "ProductPurchasePrice"),
            Statistic("CHANGED", 3, 30m, 8m, 24m, "ProductPurchasePrice"),
            Statistic("NEW", 1, 10m, 8m, 8m, "ProductPurchasePrice"),
            Statistic("SHARED", 2, 20m, 8m, 16m, "ProductPurchasePrice"),
            sharedNewStore,
            Statistic("OPENITEM", 2, 20m, null, null, "OpenItemMissingPrice"),
            Statistic("MIXED", 2, 20m, null, null, "IdentityConflict"),
        };

        var codes = SalesStatisticsProductStoreDailyCommandWriter.ResolveCurrentCostProductCodes(
            old[0].Date, rebuilt.Select(row => row.ProductCode).ToList(), rebuilt, old);

        Assert.Equal(new[] { "CHANGED", "MISSING", "MIXED", "NEW", "OPENITEM", "SHARED" }, codes);
    }

    [Fact]
    public void 国内货的行键编码在国内编码族内变化时仍沿用旧成本快照()
    {
        var chinaCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN-A", "CN-B" };
        var old = new[]
        {
            // 旧写法的 200 行 -> 本次直写 CN-A。
            Statistic("TO-DIRECT", 2, 20m, 3m, 6m, "StoreRetailPrice", "200"),
            // 归属中途变更：旧行直写 CN-A -> 本次直写 CN-B。
            Statistic("MOVED", 2, 20m, 4m, 8m, "StoreRetailPrice", "CN-A"),
            // 开关关闭后的回退：旧行直写 CN-A -> 本次写回 200。
            Statistic("BACK-TO-200", 2, 20m, 5m, 10m, "StoreRetailPrice", "CN-A"),
            // 销售事实变了：只能沿用旧单价，不能沿用旧总成本。
            Statistic("FACTS-CHANGED", 2, 20m, 6m, 12m, "StoreRetailPrice", "200"),
        };
        var rebuilt = new[]
        {
            Statistic("TO-DIRECT", 2, 20m, 99m, 198m, "StoreRetailPrice", "CN-A"),
            Statistic("MOVED", 2, 20m, 99m, 198m, "StoreRetailPrice", "CN-B"),
            Statistic("BACK-TO-200", 2, 20m, 99m, 198m, "StoreRetailPrice", "200"),
            Statistic("FACTS-CHANGED", 3, 30m, 99m, 297m, "StoreRetailPrice", "CN-A"),
        };

        // 旧快照都找得到，这些商品不依赖当前进价，不需要再取成本业务锁。
        var codes = SalesStatisticsProductStoreDailyCommandWriter.ResolveCurrentCostProductCodes(
            old[0].Date, rebuilt.Select(row => row.ProductCode).ToList(), rebuilt, old, chinaCodes);
        Assert.Empty(codes);

        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(rebuilt, old, chinaCodes);

        // 99 是「当前进价」。键对不上时它会覆盖历史成本，且无法恢复。
        Assert.Equal(new decimal?[] { 6m, 8m, 10m, 18m }, rebuilt.Select(row => row.TotalCost));
        Assert.Equal(new decimal?[] { 3m, 4m, 5m, 6m }, rebuilt.Select(row => row.UnitCostSnapshot));
        // 只沿用成本，本次写出的归属不变。
        Assert.Equal(new[] { "CN-A", "CN-B", "200", "CN-A" }, rebuilt.Select(row => row.SupplierCode));
    }

    [Fact]
    public void 国内编码族之外的供应商行键变化不做折叠匹配()
    {
        var chinaCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN-A" };
        var old = new[]
        {
            Statistic("AU-TO-CHINA", 2, 20m, 3m, 6m, "StoreRetailPrice", "105"),
            Statistic("CHINA-TO-AU", 2, 20m, 4m, 8m, "StoreRetailPrice", "200"),
        };
        var rebuilt = new[]
        {
            // 同一商品编码换了澳洲供应商，分店成本身份不同，不能沿用对方的成本快照。
            Statistic("AU-TO-CHINA", 2, 20m, 9m, 18m, "StoreRetailPrice", "CN-A"),
            Statistic("CHINA-TO-AU", 2, 20m, 9m, 18m, "StoreRetailPrice", "105"),
        };

        var codes = SalesStatisticsProductStoreDailyCommandWriter.ResolveCurrentCostProductCodes(
            old[0].Date, rebuilt.Select(row => row.ProductCode).ToList(), rebuilt, old, chinaCodes);
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(rebuilt, old, chinaCodes);

        Assert.Equal(new[] { "AU-TO-CHINA", "CHINA-TO-AU" }, codes);
        Assert.Equal(new decimal?[] { 18m, 18m }, rebuilt.Select(row => row.TotalCost));
    }

    [Fact]
    public void 未提供国内编码目录时行键仍按精确匹配()
    {
        var old = new[] { Statistic("P1", 2, 20m, 3m, 6m, "StoreRetailPrice", "200") };
        var rebuilt = new[] { Statistic("P1", 2, 20m, 9m, 18m, "StoreRetailPrice", "CN-A") };

        var codes = SalesStatisticsProductStoreDailyCommandWriter.ResolveCurrentCostProductCodes(
            old[0].Date, ["P1"], rebuilt, old);
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(rebuilt, old);

        Assert.Equal(new[] { "P1" }, codes);
        Assert.Equal(18m, rebuilt[0].TotalCost);
    }

    [Fact]
    public void 同一商品在国内编码族内有多条旧行时优先取200那行()
    {
        var chinaCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN-A", "CN-B" };
        var old = new[]
        {
            Statistic("P1", 1, 10m, 7m, 7m, "StoreRetailPrice", "CN-B"),
            Statistic("P1", 1, 10m, 3m, 3m, "StoreRetailPrice", "200"),
        };
        var rebuilt = new[] { Statistic("P1", 2, 20m, 9m, 18m, "StoreRetailPrice", "CN-A") };

        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(rebuilt, old, chinaCodes);

        // 事实已变（两行合成一行），沿用选中旧行的历史单价 3，而不是当前进价 9。
        Assert.Equal(3m, rebuilt[0].UnitCostSnapshot);
        Assert.Equal(6m, rebuilt[0].TotalCost);
    }

    [Fact]
    public void 当日统计仍保护新旧全部商品避免当前成本发布竞态()
    {
        var rebuilt = new[] { Statistic("P1", 2, 20m, 3m, 6m, "ProductPurchasePrice") };
        var codes = SalesStatisticsProductStoreDailyCommandWriter.ResolveCurrentCostProductCodes(
            SalesStatisticsBusinessDate.Today(), ["P1", "DELETED"], rebuilt, rebuilt);

        Assert.Equal(new[] { "DELETED", "P1" }, codes);
    }

    [Fact]
    public void 历史入口不再要求队列JobId才保留成本快照()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/Infrastructure/" +
            "SalesStatisticsProductStoreDailyCommandWriter.cs"));

        Assert.Contains("if (input.TargetDate.Date < SalesStatisticsBusinessDate.Today())", source);
        Assert.DoesNotContain(
            "expectedJobId.HasValue && input.TargetDate.Date < SalesStatisticsBusinessDate.Today()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 普通商品历史正数单价缺少旧总成本时补齐派生值()
    {
        var old = Statistic(
            productCode: "P1",
            quantity: 2,
            amount: 20m,
            unitCost: 3m,
            totalCost: null,
            source: "ProductPurchasePrice");
        var rebuilt = Statistic(
            productCode: "P1",
            quantity: 2,
            amount: 20m,
            unitCost: null,
            totalCost: null,
            source: "Missing");

        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { rebuilt }, new[] { old });

        Assert.Equal(3m, rebuilt.UnitCostSnapshot);
        Assert.Equal(6m, rebuilt.TotalCost);
        Assert.Equal(14m, rebuilt.GrossProfit);
        Assert.Equal(0.7m, rebuilt.GrossMarginRate);
    }

    [Fact]
    public void 历史事实比较按落库四位金额并包含订单数()
    {
        var old = Statistic("P1", 2, 10m, 3m, 6m, "ProductPurchasePrice");
        old.GrossProfit = null;
        old.GrossMarginRate = null;
        var sameAfterStorageRounding = Statistic("P1", 2, 10.00004m, 99m, 198m, "ProductPurchasePrice");

        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { sameAfterStorageRounding }, new[] { old });

        Assert.Equal(6m, sameAfterStorageRounding.TotalCost);
        Assert.Equal(4.00004m, sameAfterStorageRounding.GrossProfit);
        Assert.Equal(4.00004m / 10.00004m, sameAfterStorageRounding.GrossMarginRate);

        var changedOrderCount = Statistic("P1", 2, 10m, 99m, 198m, "ProductPurchasePrice");
        changedOrderCount.OrderCount = 2;
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { changedOrderCount }, new[] { old });

        Assert.Equal(3m, changedOrderCount.UnitCostSnapshot);
        Assert.Equal(6m, changedOrderCount.TotalCost);
        Assert.Equal(4m, changedOrderCount.GrossProfit);
    }

    [Fact]
    public void 已有合法零值派生字段时完整保留零值()
    {
        var old = Statistic("P0", 1, 0m, 0m, 0m, "ProductPurchasePrice");
        old.GrossProfit = 0m;
        old.GrossMarginRate = 0m;
        var rebuilt = Statistic("P0", 1, 0m, 8m, 8m, "ProductPurchasePrice");

        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { rebuilt }, new[] { old });

        Assert.Equal(0m, rebuilt.TotalCost);
        Assert.Equal(0m, rebuilt.GrossProfit);
        Assert.Equal(0m, rebuilt.GrossMarginRate);
    }

    [Fact]
    public void OpenItem混合原价有可信新明细时保留逐行重算结果并允许单价为空()
    {
        var old = Statistic(
            productCode: "OPENITEM",
            quantity: 3,
            amount: 30m,
            unitCost: null,
            totalCost: 8m,
            source: "OpenItem");
        var rebuilt = Statistic(
            productCode: "OPENITEM",
            quantity: 3,
            amount: 30m,
            unitCost: null,
            totalCost: 16m,
            source: "OpenItem");
        rebuilt.GrossProfit = 14m;
        rebuilt.GrossMarginRate = 14m / 30m;

        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { rebuilt }, new[] { old });

        Assert.Equal("OpenItem", rebuilt.CostSource);
        Assert.Null(rebuilt.UnitCostSnapshot);
        Assert.Equal(16m, rebuilt.TotalCost);
        Assert.Equal(14m, rebuilt.GrossProfit);
    }

    [Fact]
    public void OpenItem原价证据暂缺且事实未变时保留已审计旧成本_事实变化时留下缺口()
    {
        var old = Statistic(
            productCode: "OPENITEM",
            quantity: 2,
            amount: 20m,
            unitCost: null,
            totalCost: 8m,
            source: "OpenItem");
        old.GrossProfit = 12m;
        old.GrossMarginRate = 0.6m;

        var unchanged = Statistic(
            productCode: "OPENITEM",
            quantity: 2,
            amount: 20m,
            unitCost: null,
            totalCost: null,
            source: "OpenItemMissingOriginalSale");
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { unchanged }, new[] { old });
        Assert.Equal(8m, unchanged.TotalCost);
        Assert.Equal(12m, unchanged.GrossProfit);

        var sourceChanged = Statistic(
            productCode: "OPENITEM",
            quantity: 2,
            amount: 20m,
            unitCost: null,
            totalCost: null,
            source: "OpenItemMissingOriginalSale");
        sourceChanged.LastSourceUploadTime = new DateTime(2025, 1, 2, 0, 0, 1);
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { sourceChanged }, new[] { old });
        Assert.Null(sourceChanged.TotalCost);

        var conflicting = Statistic(
            productCode: "OPENITEM",
            quantity: 2,
            amount: 20m,
            unitCost: null,
            totalCost: null,
            source: "OpenItemPriceConflict");
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { conflicting }, new[] { old });
        Assert.Null(conflicting.TotalCost);
        Assert.Equal("OpenItemPriceConflict", conflicting.CostSource);

        var missingPrice = Statistic(
            productCode: "OPENITEM",
            quantity: 2,
            amount: 20m,
            unitCost: null,
            totalCost: null,
            source: "OpenItemMissingPrice");
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { missingPrice }, new[] { old });
        Assert.Null(missingPrice.TotalCost);
        Assert.Equal("OpenItemMissingPrice", missingPrice.CostSource);

        var identityConflict = Statistic(
            productCode: "OPENITEM",
            quantity: 2,
            amount: 20m,
            unitCost: 99m,
            totalCost: 198m,
            source: "IdentityConflict");
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { identityConflict }, new[] { old });
        Assert.Null(identityConflict.UnitCostSnapshot);
        Assert.Null(identityConflict.TotalCost);
        Assert.Equal("IdentityConflict", identityConflict.CostSource);

        var changed = Statistic(
            productCode: "OPENITEM",
            quantity: 3,
            amount: 30m,
            unitCost: null,
            totalCost: null,
            source: "OpenItem");
        SalesStatisticsProductStoreDailyCommandWriter.PreserveHistoricalCostSnapshots(
            new[] { changed }, new[] { old });
        Assert.Null(changed.UnitCostSnapshot);
        Assert.Null(changed.TotalCost);
        Assert.Null(changed.GrossProfit);
        Assert.Equal("OpenItem", changed.CostSource);
    }

    [Fact]
    public void 成本锁的2025资源先取全局再取日期()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "services/backend/BlazorApp.Api/Features/SalesStatistics/Common/" +
            "SalesStatisticsCostWriteLock.cs"));
        var globalIndex = source.IndexOf("2025-global", StringComparison.Ordinal);
        var dateIndex = source.IndexOf("date:{date.Date:yyyy-MM-dd}", StringComparison.Ordinal);

        Assert.True(globalIndex >= 0);
        Assert.True(dateIndex > globalIndex);
        Assert.Contains("LockOwner = N'Transaction'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 失败状态写入必须在日期锁内并通过初始状态CAS()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "services/backend/BlazorApp.Api/Features/SalesStatistics/ProductStoreDaily/" +
            "SalesStatisticsProductStoreDailyRefreshSlice.cs"));

        Assert.Contains("SalesStatisticsCostWriteLock.AcquireAsync(context.Db, targetDate)", source);
        Assert.Contains("IsProductStatisticFailureFenceCurrentAsync", source);
        Assert.Contains("SalesStatisticsCostWriteLock.IsBusy(originalException)", source);
        Assert.Contains("仅记录不覆盖新状态", source);
    }

    private static ProductStoreDailySalesStatistic Statistic(
        string productCode,
        int quantity,
        decimal amount,
        decimal? unitCost,
        decimal? totalCost,
        string source,
        string supplierCode = "S") => new()
        {
            Date = new DateTime(2025, 1, 2),
            BranchCode = "A",
            SupplierCode = supplierCode,
            ProductCode = productCode,
            TotalQuantity = quantity,
            TotalAmount = amount,
            OrderCount = 1,
            UnitCostSnapshot = unitCost,
            TotalCost = totalCost,
            GrossProfit = totalCost.HasValue ? amount - totalCost.Value : null,
            GrossMarginRate = totalCost.HasValue && amount > 0m
                ? (amount - totalCost.Value) / amount
                : null,
            CostSource = source,
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // linked worktree 的 .git 是指向主仓库的文件，同样是有效仓库根目录。
        while (directory != null
            && !File.Exists(Path.Combine(directory.FullName, ".git"))
            && !File.Exists(Path.Combine(directory.FullName, ".git", "HEAD")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
