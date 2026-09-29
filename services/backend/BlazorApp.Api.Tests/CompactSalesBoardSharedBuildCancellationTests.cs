using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Cache;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 紧凑看板立方体跨请求共享构建与请求取消的隔离。
/// 认证阶段 AuthSessionValidator 以 ToListAsync(RequestAborted) 查询后，SqlSugar 会把请求令牌残留在该请求 scoped 上下文的
/// Ado.CancellationToken 上。共享构建若跑在首个请求的上下文里，首个请求的客户端一中止，所有仍在线的搭车请求都会失败
/// （生产 2026-09-24 compact-sales-board 一例 TaskCanceledException）。
/// 用例断言立方体缓存与缓存代数，须与其他看板缓存测试串行执行。
/// </summary>
[Collection("SalesDashboardCache")]
public sealed class CompactSalesBoardSharedBuildCancellationTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 8, 12);
    // 代表发起请求自己的执行上下文（生产里是 HttpContext、日志作用域、Activity 等 AsyncLocal）。
    private static readonly AsyncLocal<string?> RequestFlowProbe = new();

    private readonly string _localDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;
    private readonly ConcurrentBag<DbLease> _leases = new();

    public CompactSalesBoardSharedBuildCancellationTests()
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
            typeof(Product),
            typeof(Store),
            typeof(StoreSalesStatistic),
            typeof(ProductStoreDailySalesStatistic),
            typeof(SalesStatisticRefreshState),
            typeof(ChinaSupplier)
        );
        _posmDb.CodeFirst.InitTables(typeof(PosmProductSupplierMapping));
    }

    [Fact]
    public async Task 首个请求客户端中止时搭车请求仍拿到共享构建结果且立方体照常缓存()
    {
        await SeedBoardAsync(amount: 30m);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var provider = BuildProvider(cache);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstService = ResolveService(firstScope);
        var secondService = ResolveService(secondScope);

        // 两个请求都经过认证：首个请求的客户端随后断开，第二个请求一直在线。
        using var firstAborted = new CancellationTokenSource();
        using var secondAborted = new CancellationTokenSource();
        await AuthenticateAsync(firstScope, firstAborted.Token);
        await AuthenticateAsync(secondScope, secondAborted.Token);

        var builds = 0;
        string? buildFlowProbe = "未执行";
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> gate = async () =>
        {
            Interlocked.Increment(ref builds);
            buildFlowProbe = RequestFlowProbe.Value;
            buildStarted.TrySetResult();
            await releaseBuild.Task;
        };
        firstService.CompactSalesBoardCubeBuildTestInterceptor = gate;
        secondService.CompactSalesBoardCubeBuildTestInterceptor = gate;

        var first = Task.Run(() =>
        {
            RequestFlowProbe.Value = "first-request";
            return firstService.GetCompactSalesBoardAsync(Query());
        });
        try
        {
            await buildStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            // SQLite 的异步读取同步完成：调用返回时第二个请求已挂在同一次构建上等待。
            var second = secondService.GetCompactSalesBoardAsync(Query());
            Assert.False(second.IsCompleted);

            firstAborted.Cancel();
            releaseBuild.TrySetResult();

            var board = await second.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.False(board.FromCache);
            Assert.Equal(SalesStatisticRefreshStatus.Fresh, board.StatisticStatus);
            Assert.Equal(30m, Assert.Single(board.Stores).TotalAmount);
            // 第二个请求确实搭上了首个请求发起的那一次构建，而不是自己重建。
            Assert.Equal(1, Volatile.Read(ref builds));
            // 共享构建不属于任何一个请求，不继承发起请求的执行上下文。
            Assert.Null(buildFlowProbe);

            // 首个请求自己后续的查询仍随它的请求令牌取消（控制器按客户端中止处理），只是不再连累搭车请求。
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));

            // 共享构建有自己的 DI scope，构建结束即释放；两个请求的 scope 仍由各自的请求持有。
            var buildLease = Assert.Single(_leases, lease => lease != Lease(firstScope) && lease != Lease(secondScope));
            Assert.True(buildLease.IsDisposed);
            Assert.False(Lease(firstScope).IsDisposed);
            Assert.False(Lease(secondScope).IsDisposed);
        }
        finally
        {
            releaseBuild.TrySetResult();
            await Task.WhenAny(first, Task.Delay(AsyncTestWaitSupport.DefaultTimeout));
        }

        // 构建结果按首个请求捕获的缓存代数写入：同区间的新请求直接命中立方体。
        using var thirdScope = provider.CreateScope();
        var cached = await ResolveService(thirdScope).GetCompactSalesBoardAsync(Query());
        Assert.True(cached.FromCache);
        Assert.Equal(30m, Assert.Single(cached.Stores).TotalAmount);
    }

    [Fact]
    public async Task 独立作用域构建沿用请求捕获的缓存代数_构建期间清缓存则立方体与分片都不写入()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        await SeedBoardAsync(amount: 10m);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var provider = BuildProvider(cache);
        using var firstScope = provider.CreateScope();
        var firstService = ResolveService(firstScope);
        // 请求已捕获代数、共享构建尚未读库时发生清缓存：数据照常返回，但旧代结果不得写回缓存。
        firstService.CompactSalesBoardCubeBuildTestInterceptor = () =>
        {
            SalesDashboardCacheKeys.ClearActiveKeys();
            return Task.CompletedTask;
        };

        var first = await firstService.GetCompactSalesBoardAsync(Query());
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalAmount == 20m)
            .Where(row => row.ProductCode == "P-SHARED")
            .ExecuteCommandAsync();
        using var secondScope = provider.CreateScope();
        var second = await ResolveService(secondScope).GetCompactSalesBoardAsync(Query());

        Assert.Equal(10m, Assert.Single(first.Stores).TotalAmount);
        // 统计状态未变，分片键不变：若旧代分片或立方体被写入，这里会读到缓存里的 10。
        Assert.False(second.FromCache);
        Assert.Equal(20m, Assert.Single(second.Stores).TotalAmount);
    }

    private static CompactSalesBoardQuery Query() =>
        new() { DateRange = new DateRangeDto { StartDate = Day, EndDate = Day } };

    private async Task SeedBoardAsync(decimal amount)
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = Day,
            Status = SalesStatisticRefreshStatus.Fresh,
            SourceTimeZone = "POSM_LOCAL",
            LastAggregatedAtUtc = DateTime.UtcNow,
            LastCheckedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new Store
        {
            StoreGUID = "s-shared-guid",
            StoreCode = "S-SHARED",
            StoreName = "共享构建分店",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ChinaSupplier { Guid = "cn-shared", SupplierCode = "CN-SHARED", SupplierName = "共享构建供应商" })
            .ExecuteCommandAsync();
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = Day, BranchCode = "S-SHARED", SupplierCode = "200", ProductCode = "P-SHARED",
            ProductName = "共享构建商品", TotalQuantity = 1, TotalAmount = amount, OrderCount = 1,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = "P-SHARED", LocalSupplierCode = "200", ChinaSupplierCode = "CN-SHARED",
        }).ExecuteCommandAsync();
    }

    /// <summary>与生产注册一致：每个 scope 各自一个 SqlSugarClient（同一个库文件），IMemoryCache 为单例。</summary>
    private ServiceProvider BuildProvider(IMemoryCache cache) =>
        new ServiceCollection()
            .AddSingleton(cache)
            .AddScoped(_ =>
            {
                var lease = new DbLease(_localDb.CopyNew(), _posmDb.CopyNew());
                _leases.Add(lease);
                return lease;
            })
            .AddScoped(services => CreateSqlSugarContext(services.GetRequiredService<DbLease>().Local))
            .AddScoped(services => CreatePosmSqlSugarContext(services.GetRequiredService<DbLease>().Posm))
            .AddScoped<ISalesDashboardReactService>(services => new SalesDashboardReactService(
                services.GetRequiredService<SqlSugarContext>(),
                services.GetRequiredService<POSMSqlSugarContext>(),
                Mock.Of<IMapper>(),
                NullLogger<SalesDashboardReactService>.Instance,
                services.GetRequiredService<IMemoryCache>(),
                services.GetRequiredService<IServiceScopeFactory>()
            ))
            .BuildServiceProvider();

    private static SalesDashboardReactService ResolveService(IServiceScope scope) =>
        Assert.IsType<SalesDashboardReactService>(scope.ServiceProvider.GetRequiredService<ISalesDashboardReactService>());

    private static DbLease Lease(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<DbLease>();

    /// <summary>
    /// 等价于认证阶段 ValidateWebAccessSessionAsync 的 ToListAsync(RequestAborted)：
    /// SqlSugar 把传入的令牌留在本请求上下文的 ADO 上，此后本请求不带令牌的查询都随它取消。
    /// </summary>
    private static async Task AuthenticateAsync(IServiceScope scope, CancellationToken requestAborted)
    {
        var context = scope.ServiceProvider.GetRequiredService<SqlSugarContext>();
        await context.Db.Queryable<Store>().Take(1).ToListAsync(requestAborted);
        Assert.Equal(requestAborted, context.Db.Ado.CancellationToken);
    }

    private sealed class DbLease(SqlSugarClient local, SqlSugarClient posm) : IDisposable
    {
        public SqlSugarClient Local { get; } = local;
        public SqlSugarClient Posm { get; } = posm;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            Local.Dispose();
            Posm.Dispose();
        }
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = false,
        InitKeyType = InitKeyType.Attribute,
    };

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
        foreach (var lease in _leases)
            lease.Dispose();
        _localDb.Dispose();
        _posmDb.Dispose();
        _localConnection.Dispose();
        _posmConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
    }
}
