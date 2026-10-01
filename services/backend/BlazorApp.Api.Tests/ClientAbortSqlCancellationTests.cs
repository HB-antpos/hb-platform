using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement.Infrastructure;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 按生产 ApplicationLog（2026-09-21～28）与本机实测（Microsoft.Data.SqlClient 6.1.1）还原的 SqlException 形态。
/// 令牌在 SqlClient 执行 / 读取中途触发时，驱动抛出 SqlException 而不是 OCE（经 SqlSugar 执行时实测 40/40）。
/// SqlException / SqlError 没有公开构造函数，只能反射调用驱动内部构造；驱动升级改了签名时这里会直接失败。
/// </summary>
internal static class SqlClientExceptionShapes
{
    internal const string SevereErrorMessage =
        "A severe error occurred on the current command.  The results, if any, should be discarded.";
    internal const string OperationCancelledMessage = "Operation cancelled by user.";
    internal const string TimeoutMessage =
        "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.";
    private const string BatchAbortedMessage =
        "无法运行请求，因为批处理已中止。这可能是由于从客户端发送的中止信号导致的；或者其他请求正在同一会话中运行，这会使会话处于忙状态。";

    /// <summary>令牌在执行中途触发（销售明细 9 条、分店业绩、扩展批量摘要、商品快详情）。</summary>
    internal static SqlException UserCancellation() => Create(
        Error(0, 11, SevereErrorMessage),
        Error(0, 11, OperationCancelledMessage)
    );

    /// <summary>MARS 连接上认证阶段被取消：服务端 3980「批处理已中止」+ 驱动的用户取消（容器商品查询）。</summary>
    internal static SqlException UserCancellationWithBatchAborted() => Create(
        Error(3980, 16, BatchAbortedMessage),
        Error(0, 11, OperationCancelledMessage)
    );

    /// <summary>等 Store 行锁时被取消，附带 3621「语句已终止」（Preorder 门禁两条）。</summary>
    internal static SqlException UserCancellationWhileWaitingRowLock() => Create(
        Error(0, 11, SevereErrorMessage),
        Error(0, 11, OperationCancelledMessage),
        Error(3621, 0, "语句已终止。")
    );

    /// <summary>UI 文化为 zh-Hans 时驱动给出的本地化文本（本机实测）。</summary>
    internal static SqlException UserCancellationZhHans() => Create(
        Error(0, 11, "当前命令发生服务器错误。结果(如果有)应被放弃。"),
        Error(0, 11, "用户已取消操作。")
    );

    /// <summary>SqlCommand 命令超时（Number -2）：服务端故障，必须仍按 Error 记录。</summary>
    internal static SqlException CommandTimeout() => Create(Error(-2, 11, TimeoutMessage));

    /// <summary>防御性组合：超时与取消同时出现时以超时为准。</summary>
    internal static SqlException CommandTimeoutWithCancellation() => Create(
        Error(-2, 11, TimeoutMessage),
        Error(0, 11, OperationCancelledMessage)
    );

    /// <summary>只有服务端的批处理中止、没有驱动自己追加的用户取消错误。</summary>
    internal static SqlException BatchAbortedWithoutClientCancellation() => Create(
        Error(3980, 16, BatchAbortedMessage)
    );

    internal static SqlException Deadlock() => Create(
        Error(1205, 13, "事务(进程 ID 64)与另一个进程被死锁在 锁 资源上，并且已被选作死锁牺牲品。请重新运行该事务。")
    );

    private static SqlError Error(int number, byte errorClass, string message)
    {
        var constructor = typeof(SqlError).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [
                typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string),
                typeof(string), typeof(int), typeof(Exception),
            ]
        ) ?? throw new InvalidOperationException("Microsoft.Data.SqlClient 的 SqlError 内部构造函数已变化");
        return (SqlError)constructor.Invoke([number, (byte)0, errorClass, "test-server", message, string.Empty, 0, null]);
    }

    private static SqlException Create(params SqlError[] errors)
    {
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var error in errors)
        {
            add.Invoke(collection, [error]);
        }

        // 与驱动相同的构造入口：Message 由各条错误按行拼接，和生产日志里的 ExceptionMessage 一致。
        var create = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(SqlErrorCollection), typeof(string)]
        ) ?? throw new InvalidOperationException("Microsoft.Data.SqlClient 的 SqlException.CreateException 已变化");
        return (SqlException)create.Invoke(null, [collection, "16.00.4165"])!;
    }
}

/// <summary>
/// 统一判定：请求令牌已触发 且 异常为取消形态（OCE / SqlClient 用户取消的 SqlException）。
/// </summary>
public sealed class ClientAbortDetectorTests
{
    public static TheoryData<string> CancellationShapes => new()
    {
        "OperationCanceledException",
        "TaskCanceledException",
        "SqlClient用户取消",
        "SqlClient用户取消_MARS批处理已中止",
        "SqlClient用户取消_等行锁语句已终止",
        "内层包装的SqlClient用户取消",
    };

    public static TheoryData<string> FailureShapes => new()
    {
        "SqlClient命令超时",
        "SqlClient命令超时伴随取消",
        "服务端批处理已中止但无驱动取消",
        "死锁",
        "普通异常",
    };

    [Theory]
    [MemberData(nameof(CancellationShapes))]
    public void 取消形态_请求令牌已触发时判定为客户端中止(string shape)
    {
        Assert.True(ClientAbortDetector.IsClientAbort(CreateShape(shape), Aborted()));
    }

    [Theory]
    [MemberData(nameof(CancellationShapes))]
    public void 取消形态_请求令牌未触发时不算客户端中止(string shape)
    {
        // 后台 worker 的 45 秒 CTS 超时同样产生「Operation cancelled by user」SqlException（生产 09-21 两条 Warning），
        // 请求令牌未触发时必须仍按故障处理。
        using var notAborted = new CancellationTokenSource();
        Assert.False(ClientAbortDetector.IsClientAbort(CreateShape(shape), notAborted.Token));
        Assert.False(ClientAbortDetector.IsClientAbort(CreateShape(shape), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(FailureShapes))]
    public void 非取消形态_即使请求令牌已触发也不算客户端中止(string shape)
    {
        // 客户端恰好断开时，命令超时、死锁等仍是真实故障，不能被吞掉。
        Assert.False(ClientAbortDetector.IsClientAbort(CreateShape(shape), Aborted()));
    }

    [Fact]
    public void 中文UI文化下本地化的用户取消文本同样识别()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
            Assert.True(ClientAbortDetector.IsClientAbort(SqlClientExceptionShapes.UserCancellationZhHans(), Aborted()));
            // 超时文本在 zh-Hans 下同样本地化，但仍以 Number -2 一票否决。
            Assert.False(ClientAbortDetector.IsClientAbort(SqlClientExceptionShapes.CommandTimeout(), Aborted()));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void 空异常不算客户端中止()
    {
        Assert.False(ClientAbortDetector.IsClientAbort(null, Aborted()));
    }

    internal static Exception CreateShape(string shape) => shape switch
    {
        "OperationCanceledException" => new OperationCanceledException(),
        "TaskCanceledException" => new TaskCanceledException("A task was canceled."),
        "SqlClient用户取消" => SqlClientExceptionShapes.UserCancellation(),
        "SqlClient用户取消_MARS批处理已中止" => SqlClientExceptionShapes.UserCancellationWithBatchAborted(),
        "SqlClient用户取消_等行锁语句已终止" => SqlClientExceptionShapes.UserCancellationWhileWaitingRowLock(),
        "内层包装的SqlClient用户取消" => new InvalidOperationException("查询失败", SqlClientExceptionShapes.UserCancellation()),
        "SqlClient命令超时" => SqlClientExceptionShapes.CommandTimeout(),
        "SqlClient命令超时伴随取消" => SqlClientExceptionShapes.CommandTimeoutWithCancellation(),
        "服务端批处理已中止但无驱动取消" => SqlClientExceptionShapes.BatchAbortedWithoutClientCancellation(),
        "死锁" => SqlClientExceptionShapes.Deadlock(),
        "普通异常" => new InvalidOperationException("业务校验失败"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static CancellationToken Aborted() => new(canceled: true);
}

/// <summary>
/// 业务代码自带 catch 的接口遇到 SqlClient 用户取消形态：客户端中止时不记 Warning 以上，
/// 请求令牌未触发（服务端自身取消）时仍按原路径记 Error。
/// </summary>
public sealed class ClientAbortSqlCancellationControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 销售明细_读取中途客户端中止的SqlException交给全局过滤器_服务端取消仍记错误(bool clientAborted)
    {
        using var requestAborted = new CancellationTokenSource();
        var service = new Mock<ISalesDashboardReactService>();
        service
            .Setup(item => item.GetSalesDetailReportFilteredAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<SalesDetailKind>(),
                It.IsAny<List<string>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyCollection<SalesDetailSection>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>()
            ))
            .Returns(() =>
            {
                // 令牌在查询执行中途触发（客户端切走），驱动抛出的是 SqlException 而不是 OCE。
                if (clientAborted)
                {
                    requestAborted.Cancel();
                }

                return Task.FromException<ProductReportResponseDto<SalesDetailReportDto>>(
                    SqlClientExceptionShapes.UserCancellation()
                );
            });
        var logger = new TestLogger<SalesDetailReportController>();
        var controller = new SalesDetailReportController(service.Object, Mock.Of<IUserService>(), logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = requestAborted.Token,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Role, "Admin") },
                        "TestAuth"
                    )),
                },
            },
        };

        Task<IActionResult> Call() => controller.GetSalesDetailReport(
            SalesDetailKind.Australia,
            new DateTime(2026, 9, 1),
            new DateTime(2026, 9, 7),
            cancellationToken: requestAborted.Token
        );

        if (clientAborted)
        {
            // 上抛给 ApiExceptionFilter，由它按同一判定返回 499（见 ClientAbortedRequestLoggingTests）。
            var thrown = await Assert.ThrowsAsync<SqlException>(Call);
            Assert.True(ClientAbortDetector.IsClientAbort(thrown, requestAborted.Token));
            Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        var failure = Assert.IsType<ObjectResult>(await Call());
        Assert.Equal(StatusCodes.Status500InternalServerError, failure.StatusCode);
        var error = Assert.Single(logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Equal("GetSalesDetailReport failed", error.Message);
        Assert.IsType<SqlException>(error.Exception);
    }
}

/// <summary>
/// 商品维护扫码链路（生产 09-24「获取商品快详情失败: G008475」）：快详情吞掉取消后扫码接口仍返回 200。
/// 调用方令牌已取消时整个链路上抛、不记错误；未传令牌 / 服务端取消时保持原有“记错误并返回失败”。
/// </summary>
public sealed class StoreProductMaintenanceClientAbortTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scan-abort-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TestLogger<StoreProductMaintenanceReactService> _logger = new();
    private readonly StoreProductMaintenanceReactService _service;

    public StoreProductMaintenanceClientAbortTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Product), typeof(ProductGrade), typeof(ProductSetCode), typeof(StoreClearancePrice),
            typeof(Store), typeof(StoreRetailPrice), typeof(StoreMultiCodeProduct), typeof(HBLocalSupplier)
        );
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        _service = new StoreProductMaintenanceReactService(
            context, _logger,
            Mock.Of<IAutoPricingService>(), _cache, Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>(), Mock.Of<IProductMaintenanceHqProjectionWriter>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 快详情_读取中途客户端中止时上抛且不记错误_服务端取消仍返回失败并记错误(bool clientAborted)
    {
        using var requestAborted = new CancellationTokenSource();
        InjectUserCancellationOnFastDetailQuery(requestAborted, clientAborted);

        if (clientAborted)
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _service.GetFastDetailAsync("G008475", null, null, requestAborted.Token));
            Assert.DoesNotContain(_logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        var response = await _service.GetFastDetailAsync("G008475", null, null, requestAborted.Token);
        Assert.False(response.Success);
        var error = Assert.Single(_logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.StartsWith("获取商品快详情失败", error.Message);
        Assert.IsType<SqlException>(error.Exception);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 扫码价签_快详情阶段客户端中止时整个请求上抛_服务端取消仍返回候选并记错误(bool clientAborted)
    {
        await _db.Insertable(new Product
        {
            ProductCode = "G008475",
            ProductName = "G008475",
            ItemNumber = "ITEM-8475",
            Barcode = "9300000084750",
        }).ExecuteCommandAsync();
        using var requestAborted = new CancellationTokenSource();
        InjectUserCancellationOnFastDetailQuery(requestAborted, clientAborted);
        var request = new StoreProductLookupRequestDto { Keyword = "9300000084750" };

        if (clientAborted)
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _service.ScanLabelAsync(request, null, requestAborted.Token));
            // 快详情、扫码价签两层 catch 都不再记错误。
            Assert.DoesNotContain(_logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        // 原行为：快详情失败只影响详情，扫码仍返回候选（200）并记一条快详情错误。
        var response = await _service.ScanLabelAsync(request, null, requestAborted.Token);
        Assert.True(response.Success, response.Message);
        Assert.Single(response.Data!.Candidates);
        Assert.Null(response.Data.Detail);
        var error = Assert.Single(_logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.StartsWith("获取商品快详情失败", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 扫码价签_候选查询阶段客户端中止时上抛_服务端取消仍返回失败并记错误(bool clientAborted)
    {
        using var requestAborted = new CancellationTokenSource();
        // 扫码第一条 SQL 就是候选查询（LookupAsync）。
        _db.Aop.OnLogExecuting = (_, _) =>
        {
            if (clientAborted)
            {
                requestAborted.Cancel();
            }

            throw SqlClientExceptionShapes.UserCancellation();
        };
        var request = new StoreProductLookupRequestDto { Keyword = "9300000084750" };

        if (clientAborted)
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _service.ScanLabelAsync(request, null, requestAborted.Token));
            Assert.DoesNotContain(_logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        var response = await _service.ScanLabelAsync(request, null, requestAborted.Token);
        Assert.False(response.Success);
        var error = Assert.Single(_logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.StartsWith("商品查询失败", error.Message);
    }

    /// <summary>快详情基础查询执行时注入驱动的用户取消；客户端中止场景先触发请求令牌（与生产时序一致）。</summary>
    private void InjectUserCancellationOnFastDetailQuery(CancellationTokenSource requestAborted, bool clientAborted)
    {
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.Contains("StorePriceUuid", StringComparison.Ordinal))
            {
                return;
            }

            if (clientAborted)
            {
                requestAborted.Cancel();
            }

            throw SqlClientExceptionShapes.UserCancellation();
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}

/// <summary>
/// Preorder 门禁的 fail-open 只针对“门禁不可用”。生产 09-24 分店 1020 两次提交在等 Store 行锁时客户端中止，
/// 旧逻辑记 Error「Preorder 原子门禁检查失败」后按门禁不可用放行。现在：调用方传入且已触发的请求令牌 + 取消形态 →
/// 原样上抛、不放行；未传令牌（旧购物车、PDA、Preorder 自身）或服务端故障（含命令超时）→ 保持原有 fail-open 契约。
/// </summary>
public sealed class StoreOrderPlacementGateClientAbortTests : IDisposable
{
    private const string StoreLockResource = "PreorderStoreGate:store-1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"gate-abort-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly TestLogger<StoreOrderPlacementGateCoordinator> _logger = new();
    private readonly StoreOrderPlacementGateCoordinator _coordinator;

    public StoreOrderPlacementGateClientAbortTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(Store));
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        _coordinator = new StoreOrderPlacementGateCoordinator(context, _logger);
    }

    [Fact]
    public async Task 等行锁时客户端中止_不再fail_open放行_取消原样上抛且不记错误()
    {
        using var requestAborted = new CancellationTokenSource();
        var injected = InjectOnStoreRowLock(() =>
        {
            requestAborted.Cancel();
            return SqlClientExceptionShapes.UserCancellationWhileWaitingRowLock();
        });

        var thrown = await Assert.ThrowsAsync<SqlException>(() => _coordinator.IsBlockedInsideTransactionAsync(
            new StoreOrderPlacementGateContext(StoreLockResource),
            "1020",
            "React.SubmitOrder",
            requestAborted.Token
        ));

        Assert.Same(injected.Value, thrown);
        Assert.DoesNotContain(_logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
    }

    [Fact]
    public async Task 等行锁时OCE形态的客户端中止同样不放行()
    {
        using var requestAborted = new CancellationTokenSource();
        InjectOnStoreRowLock(() =>
        {
            requestAborted.Cancel();
            return new TaskCanceledException("A task was canceled.");
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => _coordinator.IsBlockedInsideTransactionAsync(
            new StoreOrderPlacementGateContext(StoreLockResource),
            "1020",
            "React.SubmitOrder",
            requestAborted.Token
        ));
        Assert.DoesNotContain(_logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
    }

    [Fact]
    public async Task 未传请求令牌时_用户取消形态仍按门禁不可用fail_open并记错误()
    {
        // 旧购物车、PDA、CopyOrder/CreateOrder 不传令牌：行为与改动前逐字相同。
        InjectOnStoreRowLock(SqlClientExceptionShapes.UserCancellationWhileWaitingRowLock);

        var decision = await _coordinator.IsBlockedInsideTransactionAsync(
            new StoreOrderPlacementGateContext(StoreLockResource),
            "1020",
            "React.SubmitOrder"
        );

        AssertFailOpen(decision, "Preorder 原子门禁检查失败");
    }

    [Fact]
    public async Task 客户端已中止但行锁语句是命令超时_仍按门禁不可用fail_open并记错误()
    {
        // 服务端超时不能借客户端恰好断开被吞掉：仍记 Error，且沿用 fail-open 契约。
        using var requestAborted = new CancellationTokenSource();
        requestAborted.Cancel();
        InjectOnStoreRowLock(SqlClientExceptionShapes.CommandTimeout);

        var decision = await _coordinator.IsBlockedInsideTransactionAsync(
            new StoreOrderPlacementGateContext(StoreLockResource),
            "1020",
            "React.SubmitOrder",
            requestAborted.Token
        );

        AssertFailOpen(decision, "Preorder 原子门禁检查失败");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 解析门禁锁资源时客户端中止_不执行写单命令_未传令牌时仍放行(bool passRequestToken)
    {
        using var requestAborted = new CancellationTokenSource();
        // 门禁第一步是按 StoreCode 查 Store：在这条查询执行中途客户端断开。
        _db.Aop.OnLogExecuting = (_, _) =>
        {
            requestAborted.Cancel();
            throw SqlClientExceptionShapes.UserCancellation();
        };
        StoreOrderPlacementGateContext? executedWith = null;
        Task<ApiResponse<string>> Execute() => _coordinator.ExecuteWithProcessGateAsync(
            "1020",
            bypassPreorderGate: false,
            "React.SubmitOrder",
            context =>
            {
                executedWith = context;
                return Task.FromResult(ApiResponse<string>.OK("written"));
            },
            passRequestToken ? requestAborted.Token : CancellationToken.None
        );

        if (passRequestToken)
        {
            await Assert.ThrowsAsync<SqlException>(Execute);
            Assert.Null(executedWith);
            Assert.DoesNotContain(_logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        var result = await Execute();
        Assert.True(result.Success);
        Assert.False(executedWith!.RequiresEvaluation);
        Assert.Contains(_logger.Entries, entry => entry.LogLevel == LogLevel.Error
            && entry.Message.StartsWith("Preorder 门禁查询失败", StringComparison.Ordinal));
        Assert.Contains(_logger.Entries, entry => entry.LogLevel == LogLevel.Warning
            && entry.Message.StartsWith("Preorder 门禁不可用，普通订单写入已放行", StringComparison.Ordinal));
    }

    private void AssertFailOpen(StoreOrderPlacementGateDecision decision, string errorMessagePrefix)
    {
        Assert.False(decision.IsBlocked);
        var error = Assert.Single(_logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.StartsWith(errorMessagePrefix, error.Message);
        Assert.IsType<SqlException>(error.Exception);
        var warning = Assert.Single(_logger.Entries, entry => entry.LogLevel == LogLevel.Warning);
        Assert.StartsWith("Preorder 门禁不可用，普通订单写入已放行", warning.Message);
    }

    /// <summary>在门禁取 Store 身份行锁的语句（SQLite 为 no-op UPDATE）执行时抛出指定异常。</summary>
    private StrongBox<Exception?> InjectOnStoreRowLock(Func<Exception> createException)
    {
        var injected = new StrongBox<Exception?>();
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("UPDATE \"Store\"", StringComparison.Ordinal))
            {
                injected.Value = createException();
                throw injected.Value;
            }
        };
        return injected;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}

public sealed class ClientAbortDetectorSqlServerFactAttribute : FactAttribute
{
    internal const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public ClientAbortDetectorSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SqlClient 取消形态验证。";
        }
    }
}

/// <summary>
/// 用真实驱动守住判定依据：驱动升级若改了用户取消 / 超时的错误号或文本，这里会先失败。
/// 只执行 WAITFOR，不建库不写数据。
/// </summary>
[Trait("Category", "SQL")]
public sealed class ClientAbortDetectorSqlServerTests
{
    [ClientAbortDetectorSqlServerFact]
    public async Task 真实SqlClient_令牌在执行中途触发的用户取消SqlException判定为客户端中止()
    {
        // 与生产一致：请求令牌残留在 SqlSugar ADO 上，语句执行中途客户端断开。
        // 300ms 时本机实测 40/40 为 SqlException；取消若落在语句发出前则是 OCE，两种形态都必须识别。
        SqlException? observed = null;
        for (var attempt = 0; attempt < 3 && observed == null; attempt++)
        {
            using var requestAborted = new CancellationTokenSource();
            using var db = CreateClient();
            db.Ado.CancellationToken = requestAborted.Token;
            requestAborted.CancelAfter(TimeSpan.FromMilliseconds(300));

            var exception = await Record.ExceptionAsync(() => db.Ado.ExecuteCommandAsync("WAITFOR DELAY '00:00:05'"));

            Assert.NotNull(exception);
            Assert.True(ClientAbortDetector.IsClientAbort(exception, requestAborted.Token), exception.ToString());
            observed = exception as SqlException;
        }

        Assert.NotNull(observed);
        Assert.DoesNotContain(observed!.Errors.Cast<SqlError>(), error => error.Number == -2);
    }

    [ClientAbortDetectorSqlServerFact]
    public async Task 真实SqlClient_命令超时即使请求令牌已触发也不算客户端中止()
    {
        using var db = CreateClient();
        db.Ado.CommandTimeOut = 1;

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.Ado.ExecuteCommandAsync("WAITFOR DELAY '00:00:05'"));

        Assert.Contains(exception.Errors.Cast<SqlError>(), error => error.Number == -2);
        Assert.False(ClientAbortDetector.IsClientAbort(exception, new CancellationToken(canceled: true)));
    }

    private static SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = Environment.GetEnvironmentVariable(
            ClientAbortDetectorSqlServerFactAttribute.ConnectionEnvironmentVariable)!,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
    });
}
