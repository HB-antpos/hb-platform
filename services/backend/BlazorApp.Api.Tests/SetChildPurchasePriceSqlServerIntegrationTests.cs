using System.Diagnostics;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SetChildPurchasePriceSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable =
        "SET_CHILD_PURCHASE_PRICE_SQLSERVER_TEST_CONNECTION";

    public SetChildPurchasePriceSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 套装成本锁验证。";
        }
    }
}

public sealed class SetChildPurchasePriceSqlServerIntegrationTests
{
    private const string ConnectionEnvironmentVariable =
        "SET_CHILD_PURCHASE_PRICE_SQLSERVER_TEST_CONNECTION";

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 商品身份写入_允许无关成本并行且排斥同商品其他身份及全局维护()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)!;
        using var identity = CreateClient(connection);
        using var competitor = CreateClient(connection);
        var code = "IDENTITY-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        await identity.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(identity, [code], 500);

            // 无关成本事务可以真实获锁并提交，同商品仍须等待其商品独占锁。
            await competitor.Ado.BeginTranAsync();
            await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(competitor, [code + "-OTHER"], 500);
            await competitor.Ado.CommitTranAsync();
            await competitor.Ado.BeginTranAsync();
            var sameProduct = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(competitor, [code], 100));
            Assert.EndsWith(code, sameProduct.Resource);
            await competitor.Ado.RollbackTranAsync();

            // 即使编码不同，身份写入之间仍互斥；全局维护也不能穿过身份保护。
            await competitor.Ado.BeginTranAsync();
            var otherIdentity = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(competitor, [code + "-OTHER"], 100));
            Assert.Equal("HB:SetChildPurchasePrice:Gate", otherIdentity.Resource);
            await competitor.Ado.RollbackTranAsync();
            await competitor.Ado.BeginTranAsync();
            var global = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireAllWithinBudgetAsync(competitor, 100));
            Assert.Equal("HB:SetChildPurchasePrice:Gate", global.Resource);
        }
        finally
        {
            await competitor.Ado.RollbackTranAsync();
            await identity.Ado.RollbackTranAsync();
        }

        // 回滚释放身份闸门与商品锁，后续请求可继续处理同一商品。
        await competitor.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(competitor, [code], 500);
            await competitor.Ado.CommitTranAsync();
        }
        catch
        {
            await competitor.Ado.RollbackTranAsync();
            throw;
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsInBatchWithinBudgetAsync_SqlServer五千商品批次允许无关身份U和成本S完成()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var batchCodes = Enumerable.Range(0, 5_000)
            .Select(index => $"BATCH-{suffix}-{index:D4}")
            .ToArray();
        var unrelatedIdentityCode = $"BATCH-IDENTITY-{suffix}";
        var unrelatedCostCode = $"BATCH-COST-{suffix}";
        using var batchDb = CreateClient(connectionString);
        using var tailDb = CreateClient(connectionString);
        using var identityDb = CreateClient(connectionString);
        using var costDb = CreateClient(connectionString);

        await batchDb.Ado.BeginTranAsync();
        try
        {
            var scope = await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                batchDb,
                batchCodes,
                totalWaitMilliseconds: 10_000
            );

            Assert.False(scope.LocksAllProducts);
            scope.EnsureCovers(batchDb, batchCodes);

            await tailDb.Ado.BeginTranAsync();
            try
            {
                // 明确争用批次尾部编码，防止 JSON 参数或 OPENJSON 游标只处理前缀。
                var tailException = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                    SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                        tailDb,
                        new[] { batchCodes[^1] },
                        totalWaitMilliseconds: 250
                    ));
                Assert.True(tailException.Resource.EndsWith(batchCodes[^1], StringComparison.Ordinal));
                Assert.Equal(-1, tailException.ResultCode);
            }
            finally
            {
                if (tailDb.Ado.Transaction != null)
                    await tailDb.Ado.RollbackTranAsync();
            }

            // 批量统计只持 Shared 总闸和批次商品 X 锁；无关身份 U 与无关成本 S 必须可继续。
            await identityDb.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                    identityDb,
                    new[] { unrelatedIdentityCode },
                    totalWaitMilliseconds: 1_000
                );
                await identityDb.Ado.CommitTranAsync();
            }
            finally
            {
                if (identityDb.Ado.Transaction != null)
                    await identityDb.Ado.RollbackTranAsync();
            }

            await costDb.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    costDb,
                    new[] { unrelatedCostCode },
                    totalWaitMilliseconds: 1_000
                );
                await costDb.Ado.CommitTranAsync();
            }
            finally
            {
                if (costDb.Ado.Transaction != null)
                    await costDb.Ado.RollbackTranAsync();
            }
        }
        finally
        {
            if (batchDb.Ado.Transaction != null)
                await batchDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsInBatchWithinBudgetAsync_SqlServer同商品与全局独占仍阻塞()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var productCode = $"BATCH-BUSY-{suffix}";
        using var productHolderDb = CreateClient(connectionString);
        using var productWaiterDb = CreateClient(connectionString);
        using var globalHolderDb = CreateClient(connectionString);
        using var globalWaiterDb = CreateClient(connectionString);
        await productHolderDb.Ado.BeginTranAsync();
        await productWaiterDb.Ado.BeginTranAsync();
        await globalHolderDb.Ado.BeginTranAsync();
        await globalWaiterDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                productHolderDb,
                new[] { productCode },
                totalWaitMilliseconds: 0
            );
            var sameProduct = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                    productWaiterDb,
                    new[] { productCode },
                    totalWaitMilliseconds: 250
                ));
            Assert.True(sameProduct.Resource.EndsWith(productCode, StringComparison.Ordinal));
            Assert.Equal(-1, sameProduct.ResultCode);
            await productWaiterDb.Ado.RollbackTranAsync();
            await productWaiterDb.Ado.BeginTranAsync();
            await productHolderDb.Ado.RollbackTranAsync();
            await productHolderDb.Ado.BeginTranAsync();

            await SetChildPurchasePriceMutationLock.AcquireAllAsync(globalHolderDb);
            var global = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                    globalWaiterDb,
                    new[] { $"BATCH-GLOBAL-{suffix}" },
                    totalWaitMilliseconds: 250
                ));
            Assert.Equal("HB:SetChildPurchasePrice:Gate", global.Resource);
            Assert.Equal(-1, global.ResultCode);
        }
        finally
        {
            if (globalWaiterDb.Ado.Transaction != null)
                await globalWaiterDb.Ado.RollbackTranAsync();
            if (globalHolderDb.Ado.Transaction != null)
                await globalHolderDb.Ado.RollbackTranAsync();
            if (productWaiterDb.Ado.Transaction != null)
                await productWaiterDb.Ado.RollbackTranAsync();
            if (productHolderDb.Ado.Transaction != null)
                await productHolderDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsInBatchWithinBudgetAsync_SqlServer预算冲突回滚后不覆盖未获锁商品()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var availableCode = $"BATCH-A-{suffix}";
        var blockedCode = $"BATCH-Z-{suffix}";
        using var holderDb = CreateClient(connectionString);
        using var waiterDb = CreateClient(connectionString);
        using var verifyDb = CreateClient(connectionString);

        await holderDb.Ado.BeginTranAsync();
        await waiterDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                holderDb,
                new[] { blockedCode },
                totalWaitMilliseconds: 0
            );

            // 规范顺序保证 A 先拿到锁，Z 冲突后整批失败；调用方必须回滚以释放 A。
            var exception = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                    waiterDb,
                    new[] { blockedCode, availableCode },
                    totalWaitMilliseconds: 250
                ));
            Assert.True(exception.Resource.EndsWith(blockedCode, StringComparison.Ordinal));
            Assert.Equal(-1, exception.ResultCode);

            await waiterDb.Ado.RollbackTranAsync();
            await holderDb.Ado.RollbackTranAsync();

            await verifyDb.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                    verifyDb,
                    new[] { availableCode },
                    totalWaitMilliseconds: 0
                );
                await verifyDb.Ado.RollbackTranAsync();
            }
            finally
            {
                if (verifyDb.Ado.Transaction != null)
                    await verifyDb.Ado.RollbackTranAsync();
            }
        }
        finally
        {
            if (waiterDb.Ado.Transaction != null)
                await waiterDb.Ado.RollbackTranAsync();
            if (holderDb.Ado.Transaction != null)
                await holderDb.Ado.RollbackTranAsync();
            if (verifyDb.Ado.Transaction != null)
                await verifyDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsInBatchWithinBudgetAsync_SqlServer回滚释放批次商品锁()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var codes = new[] { $"BATCH-RELEASE-A-{suffix}", $"BATCH-RELEASE-B-{suffix}" };
        using var holderDb = CreateClient(connectionString);
        using var successorDb = CreateClient(connectionString);

        await holderDb.Ado.BeginTranAsync();
        try
        {
            var scope = await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                holderDb,
                codes,
                totalWaitMilliseconds: 0
            );
            scope.EnsureCovers(holderDb, codes);
            await holderDb.Ado.RollbackTranAsync();

            await successorDb.Ado.BeginTranAsync();
            try
            {
                var successorScope = await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                    successorDb,
                    codes,
                    totalWaitMilliseconds: 0
                );
                successorScope.EnsureCovers(successorDb, codes);
                await successorDb.Ado.CommitTranAsync();
            }
            finally
            {
                if (successorDb.Ado.Transaction != null)
                    await successorDb.Ado.RollbackTranAsync();
            }
        }
        finally
        {
            if (holderDb.Ado.Transaction != null)
                await holderDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 统计批量成本锁冲突_回滚部分锁后下一事务重试成功()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
        if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            dataSource = dataSource[4..];
        var host = dataSource.Split(',', 2)[0].Trim('[', ']');
        Assert.Contains(host, new[] { "127.0.0.1", "localhost", "::1" }, StringComparer.OrdinalIgnoreCase);

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var availableCode = $"RETRY-A-{suffix}";
        var blockedCode = $"RETRY-Z-{suffix}";
        var codes = new[] { blockedCode, availableCode };
        using var holderDb = CreateClient(connectionString);
        using var writerDb = CreateClient(connectionString);
        using var verifyDb = CreateClient(connectionString);
        var attempts = 0;
        var rollbackConfirmed = false;
        var partialLockObserved = false;
        var releaseChecked = false;
        var expected = new SalesStatisticsProductStoreDailyCommandWriter.PersistResult(
            new SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult("Fresh", null),
            null);

        await holderDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                holderDb, new[] { blockedCode }, totalWaitMilliseconds: 0);

            var actual = await SalesStatisticsProductStoreDailyCommandWriter.PersistWithLockRetryAsync(
                persistOnceAsync: async () =>
                {
                    attempts++;
                    Assert.Null(writerDb.Ado.Transaction);
                    rollbackConfirmed = false;
                    await SalesStatisticsTransactionExecutor.ExecuteAsync(
                        beginAsync: () => writerDb.Ado.BeginTranAsync(),
                        workAsync: async () =>
                        {
                            var scope = await SetChildPurchasePriceMutationLock
                                .AcquireProductsInBatchWithinBudgetAsync(
                                    writerDb, codes, totalWaitMilliseconds: 100);
                            scope.EnsureCovers(writerDb, codes);
                        },
                        commitAsync: () => writerDb.Ado.CommitTranAsync(),
                        rollbackAsync: async () =>
                        {
                            // 冲突发生时第一轮仍持有 A；回滚前第三连接不能越过这把部分锁。
                            await verifyDb.Ado.BeginTranAsync();
                            try
                            {
                                var partialLock = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                                    SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(
                                        verifyDb, new[] { availableCode }, totalWaitMilliseconds: 0));
                                Assert.Equal(-1, partialLock.ResultCode);
                                partialLockObserved = true;
                            }
                            finally
                            {
                                if (verifyDb.Ado.Transaction != null)
                                    await verifyDb.Ado.RollbackTranAsync();
                            }
                            await writerDb.Ado.RollbackTranAsync();
                            rollbackConfirmed = writerDb.Ado.Transaction == null;
                        },
                        logger: NullLogger.Instance,
                        operationName: "测试统计批量成本锁重试");
                    return expected;
                },
                canRetryAfterRollback: () => rollbackConfirmed && writerDb.Ado.Transaction == null,
                targetDate: new DateTime(2026, 9, 20),
                logger: NullLogger.Instance,
                delayAsync: async delay =>
                {
                    Assert.Equal(TimeSpan.FromSeconds(1), delay);
                    Assert.Equal(1, attempts);
                    Assert.True(rollbackConfirmed);
                    Assert.Null(writerDb.Ado.Transaction);

                    // 第三连接能立即取得 A，证明第一轮已持有的部分锁随回滚释放。
                    await verifyDb.Ado.BeginTranAsync();
                    try
                    {
                        var scope = await SetChildPurchasePriceMutationLock
                            .AcquireProductsInBatchWithinBudgetAsync(
                                verifyDb, new[] { availableCode }, totalWaitMilliseconds: 0);
                        scope.EnsureCovers(verifyDb, new[] { availableCode });
                        releaseChecked = true;
                    }
                    finally
                    {
                        if (verifyDb.Ado.Transaction != null)
                            await verifyDb.Ado.RollbackTranAsync();
                    }
                    // 持锁连接此时才退出，第二轮必须新开事务才能取得整批锁。
                    await holderDb.Ado.RollbackTranAsync();
                });

            Assert.Same(expected, actual);
            Assert.Equal(2, attempts);
            Assert.True(partialLockObserved);
            Assert.True(releaseChecked);
            Assert.Null(writerDb.Ado.Transaction);
        }
        finally
        {
            if (verifyDb.Ado.Transaction != null)
                await verifyDb.Ado.RollbackTranAsync();
            if (writerDb.Ado.Transaction != null)
                await writerDb.Ado.RollbackTranAsync();
            if (holderDb.Ado.Transaction != null)
                await holderDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductIdentitiesWithinBudgetAsync_SqlServer同一事务U锁可重入()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var productCode = $"IDENTITY-REENTER-{Guid.NewGuid():N}".ToUpperInvariant();
        using var db = CreateClient(connectionString);
        await db.Ado.BeginTranAsync();
        try
        {
            var first = await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                db,
                new[] { productCode },
                totalWaitMilliseconds: 500
            );
            var second = await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                db,
                new[] { productCode },
                totalWaitMilliseconds: 500
            );

            first.EnsureCovers(db, new[] { productCode });
            second.EnsureCovers(db, new[] { productCode });
            await db.Ado.RollbackTranAsync();
        }
        finally
        {
            if (db.Ado.Transaction != null)
                await db.Ado.RollbackTranAsync();
        }
    }

    [Fact]
    public void NormalizeProductCodes_忽略大小写空白重复并稳定排序()
    {
        var result = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
            new string?[] { " b ", "A", null, "a", "" }
        );

        Assert.Equal(new[] { "A", "B" }, result);
    }

    [Fact]
    public void TryResolveConflictResultCode_普通取消不映射为业务锁冲突()
    {
        var exception = new InvalidOperationException(
            "outer",
            new OperationCanceledException("cancelled")
        );

        var matched = SetChildPurchasePriceMutationLock.TryResolveConflictResultCode(
            exception,
            out var resultCode
        );

        Assert.False(matched);
        Assert.Equal(0, resultCode);
        Assert.False(
            SetChildPurchasePriceMutationLock.TryResolveConflictResultCode(
                new InvalidOperationException("ordinary"),
                out _
            )
        );
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsWithinBudgetAsync_SqlServer不同商品可并行获取()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var firstCode = $"BUDGET-A-{suffix}";
        var secondCode = $"BUDGET-B-{suffix}";
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task AcquireAndCommitAsync(ISqlSugarClient db, string productCode)
        {
            await start.Task;
            await db.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    db,
                    new[] { productCode },
                    totalWaitMilliseconds: 1_000
                );
                await Task.Delay(100);
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        var firstTask = AcquireAndCommitAsync(firstDb, firstCode);
        var secondTask = AcquireAndCommitAsync(secondDb, secondCode);
        start.SetResult();

        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsWithinBudgetAsync_SqlServer同商品阻塞并抛出原锁异常()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var productCode = $"BUDGET-BUSY-{Guid.NewGuid():N}".ToUpperInvariant();
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await firstDb.Ado.BeginTranAsync();
        await secondDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                firstDb,
                new[] { productCode },
                totalWaitMilliseconds: 1_000
            );

            var startedAt = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    secondDb,
                    new[] { productCode },
                    totalWaitMilliseconds: 250
                )
            );

            Assert.True(exception.Resource.EndsWith(productCode, StringComparison.Ordinal));
            Assert.True(startedAt.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally
        {
            await secondDb.Ado.RollbackTranAsync();
            await firstDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsWithinBudgetAsync_SqlServer总闸阻塞并抛出原锁异常()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var productCode = $"BUDGET-GATE-{Guid.NewGuid():N}".ToUpperInvariant();
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await firstDb.Ado.BeginTranAsync();
        await secondDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireAllWithinBudgetAsync(
                firstDb,
                totalWaitMilliseconds: 1_000
            );

            var startedAt = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    secondDb,
                    new[] { productCode },
                    totalWaitMilliseconds: 250
                )
            );

            Assert.Equal("HB:SetChildPurchasePrice:Gate", exception.Resource);
            Assert.True(startedAt.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally
        {
            await secondDb.Ado.RollbackTranAsync();
            await firstDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsWithinBudgetAsync_SqlServer总预算覆盖总闸和多个商品锁等待()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var firstCode = $"BUDGET-TOTAL-A-{suffix}";
        var blockedCode = $"BUDGET-TOTAL-B-{suffix}";
        using var productHolderDb = CreateClient(connectionString);
        using var gateHolderDb = CreateClient(connectionString);
        using var waitingDb = CreateClient(connectionString);
        using var releasedProductDb = CreateClient(connectionString);
        await productHolderDb.Ado.BeginTranAsync();
        await gateHolderDb.Ado.BeginTranAsync();
        await waitingDb.Ado.BeginTranAsync();
        try
        {
            var productLockResult = await TryAcquireAsync(
                productHolderDb,
                "HB:SetChildPurchasePrice:Product:" + blockedCode,
                "Exclusive"
            );
            Assert.True(productLockResult >= 0);
            await SetChildPurchasePriceMutationLock.AcquireAllWithinBudgetAsync(
                gateHolderDb,
                totalWaitMilliseconds: 1_000
            );

            var startedAt = Stopwatch.StartNew();
            var acquireTask = SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                waitingDb,
                new[] { blockedCode, firstCode },
                totalWaitMilliseconds: 600
            );

            await Task.Delay(300);
            await gateHolderDb.Ado.RollbackTranAsync();

            var exception = await Assert.ThrowsAsync<SetChildPurchasePriceLockException>(() =>
                acquireTask
            );

            Assert.True(exception.Resource.EndsWith(blockedCode, StringComparison.Ordinal));
            // 总闸已等待约300ms，剩余预算约300ms；逐锁各自使用600ms的实现将约耗时900ms。
            Assert.True(startedAt.Elapsed < TimeSpan.FromMilliseconds(800));

            // A 已在 B 阻塞前取得；全有或全无入口失败后由调用方回滚，A 才应被释放。
            await waitingDb.Ado.RollbackTranAsync();
            await releasedProductDb.Ado.BeginTranAsync();
            await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                releasedProductDb,
                new[] { firstCode },
                totalWaitMilliseconds: 0
            );
            await releasedProductDb.Ado.RollbackTranAsync();
        }
        finally
        {
            if (waitingDb.Ado.Transaction != null)
            {
                await waitingDb.Ado.RollbackTranAsync();
            }
            if (gateHolderDb.Ado.Transaction != null)
            {
                await gateHolderDb.Ado.RollbackTranAsync();
            }
            if (releasedProductDb.Ado.Transaction != null)
            {
                await releasedProductDb.Ado.RollbackTranAsync();
            }
            await productHolderDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsWithinBudgetAsync_SqlServer反向输入按稳定顺序获取且不死锁()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var firstCode = $"BUDGET-ORDER-A-{suffix}";
        var secondCode = $"BUDGET-ORDER-B-{suffix}";
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task AcquireAndCommitAsync(ISqlSugarClient db, string[] productCodes)
        {
            await start.Task;
            await db.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    db,
                    productCodes,
                    totalWaitMilliseconds: 2_000
                );
                await Task.Delay(100);
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        var firstTask = AcquireAndCommitAsync(firstDb, new[] { firstCode, secondCode });
        var secondTask = AcquireAndCommitAsync(secondDb, new[] { secondCode, firstCode });
        start.SetResult();

        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsWithinBudgetAsync_SqlServer提交和回滚都会释放商品锁()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var productCode = $"BUDGET-RELEASE-{Guid.NewGuid():N}".ToUpperInvariant();
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        using var thirdDb = CreateClient(connectionString);
        using var fourthDb = CreateClient(connectionString);

        await firstDb.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
            firstDb,
            new[] { productCode },
            totalWaitMilliseconds: 0
        );
        await firstDb.Ado.CommitTranAsync();

        await secondDb.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
            secondDb,
            new[] { productCode },
            totalWaitMilliseconds: 0
        );
        await secondDb.Ado.CommitTranAsync();

        await thirdDb.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
            thirdDb,
            new[] { productCode },
            totalWaitMilliseconds: 0
        );
        await thirdDb.Ado.RollbackTranAsync();

        await fourthDb.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
            fourthDb,
            new[] { productCode },
            totalWaitMilliseconds: 0
        );
        await fourthDb.Ado.RollbackTranAsync();
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsAsync_SqlServer同商品锁跨连接互斥()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var productCode = $"LOCK-{Guid.NewGuid():N}".ToUpperInvariant();
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await firstDb.Ado.BeginTranAsync();
        await secondDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                firstDb,
                new[] { productCode }
            );

            var gateResult = await TryAcquireAsync(
                secondDb,
                "HB:SetChildPurchasePrice:Gate",
                "Shared"
            );
            var productResult = await TryAcquireAsync(
                secondDb,
                "HB:SetChildPurchasePrice:Product:" + productCode,
                "Exclusive"
            );

            Assert.True(gateResult >= 0);
            Assert.True(productResult < 0);
        }
        finally
        {
            await secondDb.Ado.RollbackTranAsync();
            await firstDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsPartiallyAsync_SqlServer共享总预算且保留已成功商品锁()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var availableCode = $"LOCK-A-{suffix}";
        var busyCode = $"LOCK-B-{suffix}";
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await firstDb.Ado.BeginTranAsync();
        await secondDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                firstDb,
                new[] { busyCode }
            );
            var startedAt = DateTime.UtcNow;

            var result = await SetChildPurchasePriceMutationLock.AcquireProductsPartiallyAsync(
                secondDb,
                new[] { busyCode, availableCode },
                totalWaitMilliseconds: 250
            );

            Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { busyCode }, result.BusyProductCodes);
            result.LockScope.EnsureCovers(secondDb, new[] { availableCode });
            Assert.Throws<InvalidOperationException>(() =>
            {
                result.LockScope.EnsureCovers(secondDb, new[] { busyCode });
            });
        }
        finally
        {
            await secondDb.Ado.RollbackTranAsync();
            await firstDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsPartiallyAsync_SqlServer总闸等待也计入共享预算()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var productCode = $"LOCK-GATE-{Guid.NewGuid():N}".ToUpperInvariant();
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await firstDb.Ado.BeginTranAsync();
        await secondDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireAllAsync(firstDb);
            var startedAt = DateTime.UtcNow;

            var result = await SetChildPurchasePriceMutationLock.AcquireProductsPartiallyAsync(
                secondDb,
                new[] { productCode },
                totalWaitMilliseconds: 250
            );

            Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { productCode }, result.BusyProductCodes);
            Assert.Throws<InvalidOperationException>(() =>
            {
                result.LockScope.EnsureCovers(secondDb, new[] { productCode });
            });
        }
        finally
        {
            await secondDb.Ado.RollbackTranAsync();
            await firstDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireAllAsync_SqlServer全量锁阻止普通共享总闸()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await firstDb.Ado.BeginTranAsync();
        await secondDb.Ado.BeginTranAsync();
        try
        {
            await SetChildPurchasePriceMutationLock.AcquireAllAsync(firstDb);

            var result = await TryAcquireAsync(
                secondDb,
                "HB:SetChildPurchasePrice:Gate",
                "Shared"
            );

            Assert.True(result < 0);
        }
        finally
        {
            await secondDb.Ado.RollbackTranAsync();
            await firstDb.Ado.RollbackTranAsync();
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsAsync_SqlServer反向输入按稳定顺序获取且不死锁()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var firstCode = $"LOCK-A-{suffix}";
        var secondCode = $"LOCK-B-{suffix}";
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task AcquireAndCommitAsync(ISqlSugarClient db, string[] productCodes)
        {
            await start.Task;
            await db.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductsAsync(db, productCodes);
                await Task.Delay(100);
                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        var firstTask = AcquireAndCommitAsync(firstDb, new[] { firstCode, secondCode });
        var secondTask = AcquireAndCommitAsync(secondDb, new[] { secondCode, firstCode });
        start.SetResult();

        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task AcquireProductsAsync_SqlServer锁内重读保证最终成本来自最后提交的源价格()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var productCode = $"RACE-{suffix}";
        var tableName = $"SetChildCostRace_{suffix}";
        using var setupDb = CreateClient(connectionString);
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);

        await setupDb.Ado.ExecuteCommandAsync(
            $"CREATE TABLE [{tableName}] (ProductCode nvarchar(100) NOT NULL PRIMARY KEY, SourcePrice decimal(18,2) NOT NULL, DerivedCost decimal(18,2) NOT NULL);"
        );
        try
        {
            await setupDb.Ado.ExecuteCommandAsync(
                $"INSERT INTO [{tableName}] (ProductCode, SourcePrice, DerivedCost) VALUES (@ProductCode, 10, 10);",
                new SugarParameter("@ProductCode", productCode)
            );

            var firstHasLock = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var allowFirstCommit = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            async Task UpdateSourceAndDerivedAsync(
                ISqlSugarClient db,
                decimal sourcePrice,
                TaskCompletionSource? lockedSignal = null,
                Task? commitGate = null
            )
            {
                await db.Ado.BeginTranAsync();
                try
                {
                    await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        new[] { productCode }
                    );
                    lockedSignal?.SetResult();
                    await db.Ado.ExecuteCommandAsync(
                        $"UPDATE [{tableName}] SET SourcePrice = @SourcePrice WHERE ProductCode = @ProductCode;",
                        new SugarParameter("@SourcePrice", sourcePrice),
                        new SugarParameter("@ProductCode", productCode)
                    );
                    if (commitGate != null)
                    {
                        await commitGate;
                    }

                    // 关键断言场景：必须取得业务锁后再读取源值并生成派生成本。
                    var latestSource = await db.Ado.SqlQuerySingleAsync<decimal>(
                        $"SELECT SourcePrice FROM [{tableName}] WHERE ProductCode = @ProductCode;",
                        new SugarParameter("@ProductCode", productCode)
                    );
                    await db.Ado.ExecuteCommandAsync(
                        $"UPDATE [{tableName}] SET DerivedCost = @DerivedCost WHERE ProductCode = @ProductCode;",
                        new SugarParameter("@DerivedCost", latestSource),
                        new SugarParameter("@ProductCode", productCode)
                    );
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }

            var firstTask = UpdateSourceAndDerivedAsync(
                firstDb,
                20m,
                firstHasLock,
                allowFirstCommit.Task
            );
            await firstHasLock.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var secondTask = UpdateSourceAndDerivedAsync(secondDb, 30m);
            await Task.Delay(200);
            allowFirstCommit.SetResult();
            await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(15));

            var finalValues = await setupDb.Ado.SqlQuerySingleAsync<RaceResult>(
                $"SELECT SourcePrice, DerivedCost FROM [{tableName}] WHERE ProductCode = @ProductCode;",
                new SugarParameter("@ProductCode", productCode)
            );
            Assert.Equal(30m, finalValues.SourcePrice);
            Assert.Equal(30m, finalValues.DerivedCost);
        }
        finally
        {
            await setupDb.Ado.ExecuteCommandAsync($"DROP TABLE IF EXISTS [{tableName}];");
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task ContainerDetailCaseUpdate_SqlServer同一明细不同字段并发保存互不覆盖()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var tableName = $"ContainerDetailCaseRace_{suffix}";
        const string detailCode = "D-CONCURRENT";
        using var setupDb = CreateClient(connectionString);
        using var firstDb = CreateClient(connectionString);
        using var secondDb = CreateClient(connectionString);
        await setupDb.Ado.ExecuteCommandAsync(
            $"CREATE TABLE [{tableName}] (DetailCode nvarchar(100) NOT NULL PRIMARY KEY, DomesticPrice decimal(18,2) NULL, OEMPrice decimal(18,2) NULL);"
        );
        try
        {
            await setupDb.Ado.ExecuteCommandAsync(
                $"INSERT INTO [{tableName}] (DetailCode, DomesticPrice, OEMPrice) VALUES (@DetailCode, 1, 2);",
                new SugarParameter("@DetailCode", detailCode)
            );

            await firstDb.Ado.BeginTranAsync();
            try
            {
                // 与货柜保存一致：每次请求只为被接受字段生成 CASE，绝不能带回另一字段的旧值。
                await firstDb.Ado.ExecuteCommandAsync(
                    $"UPDATE [{tableName}] SET DomesticPrice = CASE WHEN DetailCode = @DetailCode THEN @DomesticPrice ELSE DomesticPrice END WHERE DetailCode IN (@DetailCode);",
                    new SugarParameter("@DetailCode", detailCode),
                    new SugarParameter("@DomesticPrice", 10m)
                );

                var secondTask = Task.Run(async () =>
                {
                    await secondDb.Ado.BeginTranAsync();
                    try
                    {
                        await secondDb.Ado.ExecuteCommandAsync(
                            $"UPDATE [{tableName}] SET OEMPrice = CASE WHEN DetailCode = @DetailCode THEN @OEMPrice ELSE OEMPrice END WHERE DetailCode IN (@DetailCode);",
                            new SugarParameter("@DetailCode", detailCode),
                            new SugarParameter("@OEMPrice", 20m)
                        );
                        await secondDb.Ado.CommitTranAsync();
                    }
                    catch
                    {
                        await secondDb.Ado.RollbackTranAsync();
                        throw;
                    }
                });

                await Task.Delay(200);
                await firstDb.Ado.CommitTranAsync();
                await secondTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch
            {
                await firstDb.Ado.RollbackTranAsync();
                throw;
            }

            var finalValues = await setupDb.Ado.SqlQuerySingleAsync<ContainerDetailCaseRaceResult>(
                $"SELECT DomesticPrice, OEMPrice FROM [{tableName}] WHERE DetailCode = @DetailCode;",
                new SugarParameter("@DetailCode", detailCode)
            );
            Assert.Equal(10m, finalValues.DomesticPrice);
            Assert.Equal(20m, finalValues.OEMPrice);
        }
        finally
        {
            await setupDb.Ado.ExecuteCommandAsync($"DROP TABLE IF EXISTS [{tableName}];");
        }
    }

    private sealed class RaceResult
    {
        public decimal SourcePrice { get; set; }
        public decimal DerivedCost { get; set; }
    }

    private sealed class ContainerDetailCaseRaceResult
    {
        public decimal DomesticPrice { get; set; }
        public decimal OEMPrice { get; set; }
    }

    private static SqlSugarClient CreateClient(string connectionString) => new(new ConnectionConfig
    {
        ConnectionString = connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
    });

    private static Task<int> TryAcquireAsync(
        ISqlSugarClient db,
        string resource,
        string lockMode
    ) => db.Ado.SqlQuerySingleAsync<int>(
        """
        DECLARE @Result int;
        EXEC @Result = sys.sp_getapplock
            @Resource = @Resource,
            @LockMode = @LockMode,
            @LockOwner = N'Transaction',
            @LockTimeout = 0;
        SELECT @Result;
        """,
        new SugarParameter("@Resource", resource),
        new SugarParameter("@LockMode", lockMode)
    );
}
