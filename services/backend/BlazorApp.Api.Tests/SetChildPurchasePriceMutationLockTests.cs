using BlazorApp.Api.Services.React;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SetChildPurchasePriceMutationLockTests
{
    [Fact]
    public async Task AcquireAllWithinBudgetAsync_SQLite事务内零预算仍返回全量锁范围()
    {
        using var db = CreateSqliteClient();
        await db.Ado.BeginTranAsync();
        try
        {
            var result = await SetChildPurchasePriceMutationLock.AcquireAllWithinBudgetAsync(
                db,
                totalWaitMilliseconds: 0
            );

            Assert.True(result.LocksAllProducts);
            result.EnsureCovers(db, new[] { "A", "B" });
        }
        finally
        {
            await db.Ado.RollbackTranAsync();
        }
    }

    [Fact]
    public async Task AcquireProductsWithinBudgetAsync_SQLite按稳定顺序规范商品并拒绝负预算()
    {
        using var db = CreateSqliteClient();
        await db.Ado.BeginTranAsync();
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    db,
                    new[] { " b ", "A", "a" },
                    totalWaitMilliseconds: -1
                )
            );

            var result = await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                db,
                new[] { " b ", "A", "a" },
                totalWaitMilliseconds: 0
            );

            Assert.False(result.LocksAllProducts);
            result.EnsureCovers(db, new[] { "A", "B" });
            Assert.Throws<InvalidOperationException>(() => result.EnsureCovers(db, new[] { "C" }));
        }
        finally
        {
            await db.Ado.RollbackTranAsync();
        }
    }

    [Fact]
    public async Task AcquireProductsWithinBudgetAsync_事务外拒绝获取锁()
    {
        using var db = CreateSqliteClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                db,
                new[] { "A" },
                totalWaitMilliseconds: 0
            )
        );
    }

    [Fact]
    public void ShouldRetryLockTimeout_只重试业务锁等待超时且受次数上限约束()
    {
        var timeout = new SetChildPurchasePriceLockException("HB:SetChildPurchasePrice:Product:A", -1);

        Assert.True(SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(timeout, 0, 4));
        Assert.True(SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(timeout, 3, 4));
        Assert.False(SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(timeout, 4, 4));
        // 服务层或 ORM 再包一层时仍应识别为可重试的锁超时。
        Assert.True(
            SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(
                new InvalidOperationException("包装", timeout),
                0,
                4
            )
        );
        // 死锁交给 1205 重试、取消保持原语义，都不能走锁超时重试。
        Assert.False(
            SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(
                new SetChildPurchasePriceLockException("deadlock", -3),
                0,
                4
            )
        );
        Assert.False(
            SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(
                new SetChildPurchasePriceLockException("cancelled", -2),
                0,
                4
            )
        );
        Assert.False(
            SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(
                new InvalidOperationException("普通业务错误"),
                0,
                4
            )
        );
        Assert.False(SetChildPurchasePriceMutationLock.ShouldRetryLockTimeout(null, 0, 4));
    }

    [Fact]
    public void ScopedBatchSetChildLockRetryDelays_总等待约一分钟且远低于网关超时()
    {
        var delays = ContainerReactService.ScopedBatchSetChildLockRetryDelays;
        // 每次尝试最多等锁 10 秒：总时长 = (重试次数 + 1) × 10 秒 + 退避间隔之和。
        var worstCase = TimeSpan.FromSeconds(10 * (delays.Length + 1))
            + TimeSpan.FromTicks(delays.Sum(delay => delay.Ticks));

        Assert.NotEmpty(delays);
        Assert.All(delays, delay => Assert.True(delay > TimeSpan.Zero));
        Assert.InRange(worstCase, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void CanContinuePartialLockFailure_仅安全的锁请求失败可在原事务继续()
    {
        Assert.True(
            SetChildPurchasePriceMutationLock.CanContinuePartialLockFailure(
                new SetChildPurchasePriceLockException(
                    "timeout",
                    -1,
                    new TimeoutException()
                )
            )
        );
        Assert.True(
            SetChildPurchasePriceMutationLock.CanContinuePartialLockFailure(
                new SetChildPurchasePriceLockException("cancelled", -2)
            )
        );
        Assert.True(
            SetChildPurchasePriceMutationLock.CanContinuePartialLockFailure(
                new SetChildPurchasePriceLockException("direct-applock-deadlock", -3)
            )
        );
        Assert.False(
            SetChildPurchasePriceMutationLock.CanContinuePartialLockFailure(
                new SetChildPurchasePriceLockException(
                    "sql-deadlock",
                    -3,
                    new InvalidOperationException("模拟 SqlException 1205 包装")
                )
            )
        );
        Assert.False(
            SetChildPurchasePriceMutationLock.CanContinuePartialLockFailure(
                new SetChildPurchasePriceLockException("invalid", -999)
            )
        );
    }

    [Fact]
    public async Task AcquireProductsPartiallyAsync_SQLite返回覆盖全部成功商品的单一锁范围()
    {
        using var db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = "DataSource=:memory:",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        await db.Ado.BeginTranAsync();
        try
        {
            var result = await SetChildPurchasePriceMutationLock.AcquireProductsPartiallyAsync(
                db,
                new[] { " b ", "A", "a" }
            );

            Assert.Empty(result.BusyProductCodes);
            result.LockScope.EnsureCovers(db, new[] { "A", "B" });
            Assert.Throws<InvalidOperationException>(() =>
            {
                result.LockScope.EnsureCovers(db, new[] { "C" });
            });
        }
        finally
        {
            await db.Ado.RollbackTranAsync();
        }
    }

    [Fact]
    public void TryResolveConflict_识别事务包装后的业务锁异常()
    {
        var original = new SetChildPurchasePriceLockException("resource", -1);
        var wrapped = new InvalidOperationException("transaction failed", original);

        var matched = SetChildPurchasePriceMutationLock.TryResolveConflict(
            wrapped,
            out var conflict
        );

        Assert.True(matched);
        Assert.Same(original, conflict);
        Assert.Equal(
            "SET_CHILD_PURCHASE_PRICE_BUSY",
            SetChildPurchasePriceMutationLock.BusyErrorCode
        );
    }

    [Fact]
    public void TryResolveConflict_普通异常不识别为锁冲突()
    {
        var matched = SetChildPurchasePriceMutationLock.TryResolveConflict(
            new InvalidOperationException("ordinary"),
            out var conflict
        );

        Assert.False(matched);
        Assert.Null(conflict);
    }

    [Fact]
    public void TryResolveConflict_普通取消不识别为业务锁冲突()
    {
        var matched = SetChildPurchasePriceMutationLock.TryResolveConflict(
            new InvalidOperationException(
                "outer",
                new OperationCanceledException("request cancelled")
            ),
            out var conflict
        );

        Assert.False(matched);
        Assert.Null(conflict);
    }

    [Fact]
    public void TryResolveConflictResultCode_专用锁异常保留结果码()
    {
        var matched = SetChildPurchasePriceMutationLock.TryResolveConflictResultCode(
            new InvalidOperationException(
                "outer",
                new SetChildPurchasePriceLockException("resource", -2)
            ),
            out var resultCode
        );

        Assert.True(matched);
        Assert.Equal(-2, resultCode);
    }

    [Theory]
    [InlineData(1205, false, true, -3)]
    [InlineData(1222, false, true, -1)]
    [InlineData(-2, false, false, 0)]
    [InlineData(-2, true, true, -1)]
    [InlineData(50000, true, false, 0)]
    public void TryResolveSqlConflictResultCode_只在取业务锁阶段识别命令超时(
        int sqlErrorNumber,
        bool includeCommandTimeout,
        bool expectedMatched,
        int expectedResultCode
    )
    {
        var matched = SetChildPurchasePriceMutationLock.TryResolveSqlConflictResultCode(
            sqlErrorNumber,
            includeCommandTimeout,
            out var resultCode
        );

        Assert.Equal(expectedMatched, matched);
        Assert.Equal(expectedResultCode, resultCode);
    }

    private static SqlSugarClient CreateSqliteClient() => new(
        new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        }
    );
}
