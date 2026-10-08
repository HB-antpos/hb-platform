using System.Diagnostics;
using System.Text.Json;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 套装子项成本写入锁。所有会改变主成本、子项零售价或套装关系的事务，
/// 必须先获取总闸，再按主商品编码稳定排序获取业务键锁。
/// </summary>
internal static class SetChildPurchasePriceMutationLock
{
    internal const string BusyErrorCode = "SET_CHILD_PURCHASE_PRICE_BUSY";
    private const string GateResource = "HB:SetChildPurchasePrice:Gate";
    private const string ProductResourcePrefix = "HB:SetChildPurchasePrice:Product:";
    private const int LockTimeoutMilliseconds = 10_000;

    internal static Task<SetChildPurchasePriceLockScope> AcquireAllAsync(ISqlSugarClient db) =>
        AcquireAsync(db, Array.Empty<string?>(), lockAll: true);

    internal static Task<SetChildPurchasePriceLockScope> AcquireProductsAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes
    ) => AcquireAsync(db, productCodes, lockAll: false);

    /// <summary>
    /// 在共享总等待预算内获取全量业务锁。调用方负责在失败时回滚事务。
    /// </summary>
    internal static Task<SetChildPurchasePriceLockScope> AcquireAllWithinBudgetAsync(
        ISqlSugarClient db,
        int totalWaitMilliseconds
    ) => AcquireWithinBudgetAsync(
        db,
        Array.Empty<string?>(),
        lockAll: true,
        totalWaitMilliseconds: totalWaitMilliseconds
    );

    /// <summary>
    /// 在共享总等待预算内按规范稳定顺序获取商品业务锁。调用方负责在失败时回滚事务。
    /// </summary>
    internal static Task<SetChildPurchasePriceLockScope> AcquireProductsWithinBudgetAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes,
        int totalWaitMilliseconds
    ) => AcquireWithinBudgetAsync(
        db,
        productCodes,
        lockAll: false,
        totalWaitMilliseconds: totalWaitMilliseconds
    );

    /// <summary>
    /// 商品新建及身份变更使用 Update 总闸：与普通成本写入的 Shared 兼容，
    /// 与其他身份变更及全局维护互斥；已有编码和预生成编码仍须全部持有商品独占锁。
    /// </summary>
    internal static Task<SetChildPurchasePriceLockScope> AcquireProductIdentitiesWithinBudgetAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes,
        int totalWaitMilliseconds = LockTimeoutMilliseconds
    ) => AcquireWithinBudgetAsync(
        db,
        productCodes,
        lockAll: false,
        totalWaitMilliseconds: totalWaitMilliseconds,
        lockIdentities: true
    );

    /// <summary>
    /// 大批量统计仍按商品保护成本，但一次往返即可获取全部锁，避免为上万商品改持全局独占锁。
    /// 失败时可能已经持有部分锁，调用方必须回滚整个事务，不能继续发布统计。
    /// </summary>
    internal static async Task<SetChildPurchasePriceLockScope> AcquireProductsInBatchWithinBudgetAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes,
        int totalWaitMilliseconds = LockTimeoutMilliseconds
    )
    {
        if (db.Ado.Transaction == null)
            throw new InvalidOperationException("套装子项成本业务锁必须在数据库事务内获取");
        if (totalWaitMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(totalWaitMilliseconds));
        var normalizedCodes = NormalizeProductCodes(productCodes);
        if (normalizedCodes.Count == 0)
            throw new ArgumentException("按商品获取套装子项成本锁时，商品编码不能为空", nameof(productCodes));

        if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            var waitStopwatch = Stopwatch.StartNew();
            await AcquireDatabaseResourceWithTimeoutAsync(db, GateResource, "Shared", totalWaitMilliseconds);
            System.Data.DataTable result;
            try
            {
                // OPENJSON 的数组下标保留 .NET 已规范化的序列；不能按数据库区域排序重新排列锁。
                result = await db.Ado.GetDataTableAsync(
                    """
                    DECLARE @StartedAt datetime2 = SYSUTCDATETIME();
                    DECLARE @Code nvarchar(4000), @LockResource nvarchar(255), @Result int = 0, @Remaining int;
                    DECLARE ProductLockCursor CURSOR LOCAL FAST_FORWARD FOR
                        SELECT [value] FROM OPENJSON(@ProductCodesJson) ORDER BY CONVERT(int, [key]);
                    OPEN ProductLockCursor;
                    FETCH NEXT FROM ProductLockCursor INTO @Code;
                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        SET @LockResource = @ProductResourcePrefix + @Code;
                        SET @Remaining = @TotalWaitMilliseconds - DATEDIFF(millisecond, @StartedAt, SYSUTCDATETIME());
                        IF @Remaining < 0 SET @Remaining = 0;
                        EXEC @Result = sys.sp_getapplock
                            @Resource = @LockResource,
                            @LockMode = N'Exclusive',
                            @LockOwner = N'Transaction',
                            @LockTimeout = @Remaining;
                        IF @Result < 0 BREAK;
                        FETCH NEXT FROM ProductLockCursor INTO @Code;
                    END;
                    CLOSE ProductLockCursor;
                    DEALLOCATE ProductLockCursor;
                    SELECT @Result AS ResultCode, @LockResource AS Resource;
                    """,
                    new SugarParameter("@ProductCodesJson", JsonSerializer.Serialize(normalizedCodes)) { Size = -1 },
                    new SugarParameter("@ProductResourcePrefix", ProductResourcePrefix),
                    new SugarParameter("@TotalWaitMilliseconds", GetRemainingWaitMilliseconds(waitStopwatch, totalWaitMilliseconds))
                );
            }
            catch (Exception ex) when (TryResolveAcquireConflictResultCode(ex, out _))
            {
                TryResolveAcquireConflictResultCode(ex, out var resultCode);
                throw new SetChildPurchasePriceLockException(ProductResourcePrefix + "Batch", resultCode, ex);
            }
            var code = Convert.ToInt32(result.Rows[0]["ResultCode"]);
            if (code < 0)
                throw new SetChildPurchasePriceLockException(Convert.ToString(result.Rows[0]["Resource"])!, code);
        }
        return new SetChildPurchasePriceLockScope(db, false, normalizedCodes);
    }

    /// <summary>
    /// 为允许字段级部分成功的入口一次获取共享总闸，再按稳定顺序尝试商品锁。
    /// 所有商品共用等待预算；旧的全有或全无入口保持不变。
    /// </summary>
    internal static async Task<SetChildPurchasePricePartialLockResult> AcquireProductsPartiallyAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes,
        int totalWaitMilliseconds = LockTimeoutMilliseconds
    )
    {
        if (db.Ado.Transaction == null)
        {
            throw new InvalidOperationException("套装子项成本业务锁必须在数据库事务内获取");
        }
        if (totalWaitMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalWaitMilliseconds),
                "共享锁等待预算不能小于0"
            );
        }

        var normalizedCodes = NormalizeProductCodes(productCodes);
        if (normalizedCodes.Count == 0)
        {
            throw new ArgumentException("按商品获取套装子项成本锁时，商品编码不能为空", nameof(productCodes));
        }

        var acquiredCodes = new List<string>();
        var busyCodes = new List<string>();
        if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            var waitStopwatch = Stopwatch.StartNew();
            try
            {
                await AcquireDatabaseResourceWithTimeoutAsync(
                    db,
                    GateResource,
                    "Shared",
                    totalWaitMilliseconds
                );
            }
            catch (SetChildPurchasePriceLockException exception)
                when (CanContinuePartialLockFailure(exception))
            {
                busyCodes.AddRange(normalizedCodes);
                return new SetChildPurchasePricePartialLockResult(
                    new SetChildPurchasePriceLockScope(db, false, acquiredCodes),
                    busyCodes
                );
            }

            foreach (var productCode in normalizedCodes)
            {
                var elapsedMilliseconds = (long)waitStopwatch.Elapsed.TotalMilliseconds;
                var remainingMilliseconds = (int)
                    Math.Max(0L, (long)totalWaitMilliseconds - elapsedMilliseconds);
                try
                {
                    await AcquireDatabaseResourceWithTimeoutAsync(
                        db,
                        ProductResourcePrefix + productCode,
                        "Exclusive",
                        remainingMilliseconds
                    );
                    acquiredCodes.Add(productCode);
                }
                catch (SetChildPurchasePriceLockException exception)
                    when (CanContinuePartialLockFailure(exception))
                {
                    busyCodes.Add(productCode);
                }
            }
        }
        else
        {
            acquiredCodes.AddRange(normalizedCodes);
        }

        return new SetChildPurchasePricePartialLockResult(
            new SetChildPurchasePriceLockScope(db, false, acquiredCodes),
            busyCodes
        );
    }

    /// <summary>
    /// sp_getapplock 直接返回 -3 不会自动回滚事务，可把该请求视为 busy；
    /// SQL 1205 包装出的 -3 带 inner 且事务可能已回滚，必须向外传播并回滚整批。
    /// </summary>
    internal static bool CanContinuePartialLockFailure(
        SetChildPurchasePriceLockException exception
    ) =>
        exception.ResultCode is -1 or -2
        || (exception.ResultCode == -3 && exception.InnerException == null);

    private static async Task<SetChildPurchasePriceLockScope> AcquireAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes,
        bool lockAll
    )
    {
        if (db.Ado.Transaction == null)
        {
            throw new InvalidOperationException("套装子项成本业务锁必须在数据库事务内获取");
        }

        var normalizedCodes = NormalizeProductCodes(productCodes);
        if (!lockAll && normalizedCodes.Count == 0)
        {
            throw new ArgumentException("按商品获取套装子项成本锁时，商品编码不能为空", nameof(productCodes));
        }

        if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            await AcquireDatabaseResourceAsync(db, GateResource, lockAll ? "Exclusive" : "Shared");
            if (!lockAll)
            {
                foreach (var productCode in normalizedCodes)
                {
                    await AcquireDatabaseResourceAsync(
                        db,
                        ProductResourcePrefix + productCode,
                        "Exclusive"
                    );
                }
            }
        }

        return new SetChildPurchasePriceLockScope(db, lockAll, normalizedCodes);
    }

    private static async Task<SetChildPurchasePriceLockScope> AcquireWithinBudgetAsync(
        ISqlSugarClient db,
        IEnumerable<string?> productCodes,
        bool lockAll,
        int totalWaitMilliseconds,
        bool lockIdentities = false
    )
    {
        if (db.Ado.Transaction == null)
        {
            throw new InvalidOperationException("套装子项成本业务锁必须在数据库事务内获取");
        }
        if (totalWaitMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalWaitMilliseconds),
                "共享锁等待预算不能小于0"
            );
        }

        var normalizedCodes = NormalizeProductCodes(productCodes);
        if (!lockAll && normalizedCodes.Count == 0)
        {
            throw new ArgumentException("按商品获取套装子项成本锁时，商品编码不能为空", nameof(productCodes));
        }

        if (db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            var waitStopwatch = Stopwatch.StartNew();
            await AcquireDatabaseResourceWithTimeoutAsync(
                db,
                GateResource,
                lockAll ? "Exclusive" : lockIdentities ? "Update" : "Shared",
                GetRemainingWaitMilliseconds(waitStopwatch, totalWaitMilliseconds)
            );

            if (!lockAll)
            {
                foreach (var productCode in normalizedCodes)
                {
                    await AcquireDatabaseResourceWithTimeoutAsync(
                        db,
                        ProductResourcePrefix + productCode,
                        "Exclusive",
                        GetRemainingWaitMilliseconds(waitStopwatch, totalWaitMilliseconds)
                    );
                }
            }
        }

        return new SetChildPurchasePriceLockScope(db, lockAll, normalizedCodes);
    }

    private static int GetRemainingWaitMilliseconds(
        Stopwatch waitStopwatch,
        int totalWaitMilliseconds
    )
    {
        var elapsedMilliseconds = (long)waitStopwatch.Elapsed.TotalMilliseconds;
        return (int)Math.Max(0L, (long)totalWaitMilliseconds - elapsedMilliseconds);
    }

    private static async Task AcquireDatabaseResourceAsync(
        ISqlSugarClient db,
        string resource,
        string lockMode
    )
    {
        int result;
        try
        {
            result = await db.Ado.SqlQuerySingleAsync<int>(
                """
                DECLARE @Result int;
                EXEC @Result = sys.sp_getapplock
                    @Resource = @Resource,
                    @LockMode = @LockMode,
                    @LockOwner = N'Transaction',
                    @LockTimeout = @LockTimeout;
                SELECT @Result;
                """,
                new SugarParameter("@Resource", resource),
                new SugarParameter("@LockMode", lockMode),
                new SugarParameter("@LockTimeout", LockTimeoutMilliseconds)
            );
        }
        catch (Exception ex) when (TryResolveAcquireConflictResultCode(ex, out _))
        {
            TryResolveAcquireConflictResultCode(ex, out var resultCode);
            throw new SetChildPurchasePriceLockException(resource, resultCode, ex);
        }
        if (result < 0)
        {
            throw new SetChildPurchasePriceLockException(resource, result);
        }
    }

    /// <summary>
    /// 仅供部分成功入口传入剩余共享预算；旧锁入口继续使用固定 10 秒实现。
    /// </summary>
    private static async Task AcquireDatabaseResourceWithTimeoutAsync(
        ISqlSugarClient db,
        string resource,
        string lockMode,
        int lockTimeoutMilliseconds
    )
    {
        int result;
        try
        {
            result = await db.Ado.SqlQuerySingleAsync<int>(
                """
                DECLARE @Result int;
                EXEC @Result = sys.sp_getapplock
                    @Resource = @Resource,
                    @LockMode = @LockMode,
                    @LockOwner = N'Transaction',
                    @LockTimeout = @LockTimeout;
                SELECT @Result;
                """,
                new SugarParameter("@Resource", resource),
                new SugarParameter("@LockMode", lockMode),
                new SugarParameter("@LockTimeout", lockTimeoutMilliseconds)
            );
        }
        catch (Exception ex) when (TryResolveAcquireConflictResultCode(ex, out _))
        {
            TryResolveAcquireConflictResultCode(ex, out var resultCode);
            throw new SetChildPurchasePriceLockException(resource, resultCode, ex);
        }
        if (result < 0)
        {
            throw new SetChildPurchasePriceLockException(resource, result);
        }
    }

    internal static bool TryResolveConflictResultCode(Exception? exception, out int resultCode)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SetChildPurchasePriceLockException lockException)
            {
                resultCode = lockException.ResultCode;
                return true;
            }

            if (current is Microsoft.Data.SqlClient.SqlException sqlException)
            {
                if (
                    TryResolveSqlConflictResultCode(
                        sqlException.Number,
                        includeCommandTimeout: false,
                        out resultCode
                    )
                )
                {
                    return true;
                }
            }
        }

        resultCode = 0;
        return false;
    }

    /// <summary>
    /// 只有执行 sp_getapplock 的阶段才把取消和命令超时解释为业务锁冲突；
    /// 普通查询中的相同异常必须保留原始语义。
    /// </summary>
    private static bool TryResolveAcquireConflictResultCode(
        Exception? exception,
        out int resultCode
    )
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SetChildPurchasePriceLockException lockException)
            {
                resultCode = lockException.ResultCode;
                return true;
            }

            if (current is OperationCanceledException)
            {
                resultCode = -2;
                return true;
            }

            if (current is Microsoft.Data.SqlClient.SqlException sqlException)
            {
                if (
                    TryResolveSqlConflictResultCode(
                        sqlException.Number,
                        includeCommandTimeout: true,
                        out resultCode
                    )
                )
                {
                    return true;
                }
            }
        }

        resultCode = 0;
        return false;
    }

    /// <summary>
    /// SQL -2 只有发生在 sp_getapplock 调用内部时才属于业务锁冲突；
    /// 普通数据库命令超时必须保留原始错误语义。
    /// </summary>
    internal static bool TryResolveSqlConflictResultCode(
        int sqlErrorNumber,
        bool includeCommandTimeout,
        out int resultCode
    )
    {
        resultCode = sqlErrorNumber switch
        {
            1205 => -3,
            1222 => -1,
            -2 when includeCommandTimeout => -1,
            _ => 0,
        };
        return resultCode < 0;
    }

    /// <summary>
    /// 从事务或 ORM 包装异常中提取业务锁冲突，供普通业务入口统一返回 409。
    /// </summary>
    internal static bool TryResolveConflict(
        Exception? exception,
        out SetChildPurchasePriceLockException? conflict
    )
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SetChildPurchasePriceLockException lockException)
            {
                conflict = lockException;
                return true;
            }
        }

        if (TryResolveConflictResultCode(exception, out var resultCode))
        {
            conflict = new SetChildPurchasePriceLockException("unknown", resultCode, exception);
            return true;
        }

        conflict = null;
        return false;
    }

    /// <summary>
    /// 交互式写入在整笔事务回滚后是否可以重试：只认业务锁本身的等待超时（-1）。
    /// 死锁（-3）交给 1205 重试逻辑，取消（-2）和普通 SQL 1222 行锁超时保留原语义。
    /// </summary>
    internal static bool ShouldRetryLockTimeout(
        Exception? exception,
        int completedRetryCount,
        int maxRetryCount
    )
    {
        if (completedRetryCount >= maxRetryCount)
        {
            return false;
        }

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SetChildPurchasePriceLockException lockException)
            {
                return lockException.ResultCode == -1;
            }
        }

        return false;
    }

    internal static List<string> NormalizeProductCodes(IEnumerable<string?> productCodes) =>
        productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
}

internal sealed class SetChildPurchasePricePartialLockResult
{
    internal SetChildPurchasePricePartialLockResult(
        SetChildPurchasePriceLockScope lockScope,
        IReadOnlyList<string> busyProductCodes
    )
    {
        LockScope = lockScope;
        BusyProductCodes = busyProductCodes;
    }

    internal SetChildPurchasePriceLockScope LockScope { get; }
    internal IReadOnlyList<string> BusyProductCodes { get; }
}

internal sealed class SetChildPurchasePriceLockScope
{
    private readonly ISqlSugarClient _db;
    private readonly HashSet<string> _productCodes;

    internal SetChildPurchasePriceLockScope(
        ISqlSugarClient db,
        bool locksAllProducts,
        IEnumerable<string> productCodes
    )
    {
        _db = db;
        LocksAllProducts = locksAllProducts;
        _productCodes = productCodes.ToHashSet(StringComparer.Ordinal);
    }

    internal bool LocksAllProducts { get; }

    internal void EnsureCovers(ISqlSugarClient db, IEnumerable<string?> productCodes)
    {
        if (!ReferenceEquals(_db, db))
        {
            throw new InvalidOperationException("套装子项成本重算必须使用获取业务锁的同一数据库连接");
        }

        if (LocksAllProducts)
        {
            return;
        }

        var requestedCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(productCodes);
        if (requestedCodes.Count == 0 || requestedCodes.Any(code => !_productCodes.Contains(code)))
        {
            throw new InvalidOperationException("套装子项成本业务锁未覆盖全部待重算商品");
        }
    }
}

internal sealed class SetChildPurchasePriceLockException : Exception
{
    internal SetChildPurchasePriceLockException(
        string resource,
        int resultCode,
        Exception? innerException = null
    )
        : base("套装子项成本正在被其他操作更新，请稍后重试", innerException)
    {
        Resource = resource;
        ResultCode = resultCode;
    }

    internal string Resource { get; }
    internal int ResultCode { get; }
}
