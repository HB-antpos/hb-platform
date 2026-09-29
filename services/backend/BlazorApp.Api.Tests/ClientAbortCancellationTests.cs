using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Controllers.React.StoreOrders;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Cart;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 业务代码自带 catch (Exception) 的接口：客户端中止（RequestAborted 已触发）时返回 499 且不记 Warning 以上日志；
/// 同样的取消异常若不是客户端中止（服务端超时等），必须仍按原路径返回 500 并记 Error，不能被吞掉。
/// 生产依据：2026-09-21～28 ApplicationLog 中这些接口的 TaskCanceledException，
/// 取消信号都来自认证阶段残留在 SqlSugar ADO 上的 RequestAborted。
/// </summary>
public sealed class ClientAbortCancellationControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 商品分页_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        var httpContext = CreateHttpContext(clientAborted);
        CancellationToken passedToken = default;
        var service = new Mock<IProductReactService>();
        service
            .Setup(item => item.GetPagedListAsync(It.IsAny<ProductReactFilterDto>(), It.IsAny<CancellationToken>()))
            .Callback<ProductReactFilterDto, CancellationToken>((_, token) => passedToken = token)
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<ReactProductController>();
        var controller = new ReactProductController(
            service.Object,
            Mock.Of<IProductStoreSyncService>(),
            Mock.Of<IProductHqSyncService>(),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            logger,
            Mock.Of<ICurrentUserService>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.GetPagedList(new ProductReactFilterDto());

        // 服务层要靠这只令牌识别客户端中止，控制器必须把 RequestAborted 原样传下去。
        Assert.Equal(httpContext.RequestAborted, passedToken);
        AssertOutcome(result, logger, clientAborted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 紧凑看板_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        var service = new Mock<ISalesDashboardReactService>();
        service
            .Setup(item => item.GetCompactSalesBoardAsync(It.IsAny<CompactSalesBoardQuery>()))
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<SalesDashboardController>();
        var controller = CreateSalesDashboardController(service.Object, logger, clientAborted);

        var result = await controller.GetCompactSalesBoard(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        AssertOutcome(result, logger, clientAborted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 分店业绩_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        CancellationToken passedToken = default;
        var service = new Mock<ISalesDashboardReactService>();
        service
            .Setup(item => item.GetExecutiveBranchPerformanceAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<int?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<DateRangeDto, int?, List<string>?, CancellationToken>((_, _, _, token) => passedToken = token)
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<SalesDashboardController>();
        var controller = CreateSalesDashboardController(service.Object, logger, clientAborted);

        var result = await controller.GetExecutiveBranchPerformance(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(controller.HttpContext.RequestAborted, passedToken);
        AssertOutcome(result, logger, clientAborted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 每小时流量_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        CancellationToken passedToken = default;
        var service = new Mock<ISalesDashboardReactService>();
        service
            .Setup(item => item.GetExecutiveHourlyTrafficAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<DateRangeDto, List<string>?, CancellationToken>((_, _, token) => passedToken = token)
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<SalesDashboardController>();
        var controller = CreateSalesDashboardController(service.Object, logger, clientAborted);

        var result = await controller.GetExecutiveHourlyTraffic(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        Assert.Equal(controller.HttpContext.RequestAborted, passedToken);
        AssertOutcome(result, logger, clientAborted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 仓库商品流向汇总_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        var roleService = new Mock<IRoleService>();
        roleService
            .Setup(item => item.UserHasExactPermissionAsync("user-1", Permissions.SalesDashboard.WarehouseFlowView))
            .ReturnsAsync(ApiResponse<bool>.OK(true));
        roleService
            .Setup(item => item.GetUserPermissionSnapshotAsync("user-1"))
            .ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.OK(new UserPermissionSnapshotDto
            {
                UserGuid = "user-1",
                RoleNames = new List<string> { Permissions.SuperAdminRoleNames[0] },
                PermissionCodes = new List<string>(),
                ExactPermissionCodes = new List<string> { Permissions.SalesDashboard.WarehouseFlowView },
            }));
        var service = new Mock<IWarehouseProductFlowAnalysisService>();
        service
            .Setup(item => item.GetSummaryAsync(It.IsAny<WarehouseProductFlowAnalysisRequest>(), It.IsAny<List<string>?>()))
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<WarehouseProductFlowAnalysisController>();
        var controller = new WarehouseProductFlowAnalysisController(
            service.Object,
            logger,
            Mock.Of<IUserService>(),
            roleService.Object
        )
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(clientAborted) },
        };

        var result = await controller.GetSummary(new WarehouseProductFlowAnalysisRequest());

        service.Verify(
            item => item.GetSummaryAsync(It.IsAny<WarehouseProductFlowAnalysisRequest>(), It.IsAny<List<string>?>()),
            Times.Once
        );
        AssertOutcome(result, logger, clientAborted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 提交订单_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        CancellationToken passedToken = default;
        var accessPolicy = new Mock<IStoreOrderAccessPolicy>();
        accessPolicy
            .Setup(item => item.RequireCartWriteAsync(It.IsAny<string?>(), It.IsAny<string>()))
            .ReturnsAsync(StoreOrderAccessDecision.Allowed);
        var placement = new Mock<IStoreOrderPlacementSlice>();
        placement
            .Setup(item => item.SubmitOrderAsync(It.IsAny<SubmitStoreOrderRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<SubmitStoreOrderRequestDto, CancellationToken>((_, token) => passedToken = token)
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<StoreOrderCartController>();
        var controller = new StoreOrderCartController(
            Mock.Of<IStoreOrderCartSlice>(),
            placement.Object,
            accessPolicy.Object,
            logger
        )
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(clientAborted) },
        };

        var result = await controller.SubmitOrder(new SubmitStoreOrderRequestDto { StoreCode = "S001" });

        // 处理器要用这只令牌判断取消是否来自客户端（见 SubmitOrderHandler 的事务语义测试）。
        Assert.Equal(controller.HttpContext.RequestAborted, passedToken);
        AssertOutcome(result, logger, clientAborted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 浏览器扩展批量摘要_客户端中止返回499且不记错误_服务端取消仍记错误(bool clientAborted)
    {
        CancellationToken passedToken = default;
        var accessService = new Mock<IBrowserExtensionAccessService>();
        accessService
            .Setup(item => item.CanAccessAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        var service = new Mock<IBrowserExtensionService>();
        service
            .Setup(item => item.GetProductSummariesAsync(
                It.IsAny<BrowserExtensionProductSummaryBatchRequestDto>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<BrowserExtensionProductSummaryBatchRequestDto, CancellationToken>((_, token) => passedToken = token)
            .ThrowsAsync(new TaskCanceledException());
        var logger = new TestLogger<ReactBrowserExtensionController>();
        var controller = new ReactBrowserExtensionController(
            service.Object,
            accessService.Object,
            Mock.Of<ILocalSupplierCategoryCaptureService>(),
            logger
        )
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(clientAborted) },
        };

        var result = await controller.GetProductSummaries(new BrowserExtensionProductSummaryBatchRequestDto
        {
            StoreCode = "S001",
            SupplierCode = "240",
            ItemNumbers = new List<string> { "A1" },
        });

        Assert.Equal(controller.HttpContext.RequestAborted, passedToken);
        AssertOutcome(result, logger, clientAborted);
    }

    private static DefaultHttpContext CreateHttpContext(bool clientAborted)
    {
        // 未中止时也用可取消的令牌，才能断言控制器传下去的正是 RequestAborted 本身。
        var requestAborted = new CancellationTokenSource();
        if (clientAborted)
        {
            requestAborted.Cancel();
        }

        return new DefaultHttpContext
        {
            RequestAborted = requestAborted.Token,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") },
                "TestAuth"
            )),
        };
    }

    private static SalesDashboardController CreateSalesDashboardController(
        ISalesDashboardReactService service,
        ILogger<SalesDashboardController> logger,
        bool clientAborted
    )
    {
        var userService = new Mock<IUserService>();
        userService
            .Setup(item => item.GetUserByGuidAsync("user-1"))
            .ReturnsAsync(ApiResponse<UserDetailDto>.OK(new UserDetailDto
            {
                UserGUID = "user-1",
                Username = "tester",
                Stores = new List<UserStoreDto> { new() { StoreCode = "S1" } },
            }));

        return new SalesDashboardController(
            service,
            logger,
            userService.Object,
            Mock.Of<ISalesDashboardCacheWarmer>(),
            Mock.Of<IRoleService>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(clientAborted) },
        };
    }

    private static void AssertOutcome(IActionResult result, ITestLoggerSink logger, bool clientAborted)
    {
        if (clientAborted)
        {
            var status = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(499, status.StatusCode);
            // ApplicationLog 只收 Warning 以上：客户端中止不应再产生任何一条。
            Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        var failure = Assert.IsAssignableFrom<IStatusCodeActionResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, failure.StatusCode);
        var error = Assert.Single(logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(error.Exception);
    }
}

/// <summary>
/// 服务层没有 HttpContext：调用方令牌已取消时只上抛、不记日志，交给控制器按 499 处理；
/// 其他来源的取消（模拟服务端超时令牌）仍按原路径记录。ADO 令牌的设置模拟生产行为——
/// 认证阶段 ToListAsync(RequestAborted) 会把令牌留在同一个 scoped SqlSugar 客户端上。
/// </summary>
public sealed class ClientAbortCancellationServiceTests : IDisposable
{
    private readonly string _localDbPath = Path.Combine(Path.GetTempPath(), $"client-abort-local-{Guid.NewGuid():N}.db");
    private readonly string _posmDbPath = Path.Combine(Path.GetTempPath(), $"client-abort-posm-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;
    private readonly CancellationTokenSource _requestAborted = new();
    private readonly CancellationTokenSource _serverTimeout = new();

    public ClientAbortCancellationServiceTests()
    {
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _localConnection.Open();
        _posmConnection.Open();
        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConnectionConfig(_posmConnection.ConnectionString));
        _requestAborted.Cancel();
        _serverTimeout.Cancel();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 商品分页服务_请求令牌已取消时只上抛不记错误_其他取消仍记错误(bool clientAborted)
    {
        _localDb.Ado.CancellationToken = clientAborted ? _requestAborted.Token : _serverTimeout.Token;
        var logger = new TestLogger<ProductReactService>();
        var service = new ProductReactService(
            CreateContext<SqlSugarContext>(_localDb),
            CreateContext<HqSqlSugarContext>(_localDb),
            Mock.Of<IMapper>(),
            logger,
            Mock.Of<IHttpContextAccessor>(),
            new ProductAuditNoopHistoryService(),
            new ProductAuditSystemCurrentUserService()
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetPagedListAsync(
            new ProductReactFilterDto { PageNumber = 1, PageSize = 20 },
            CallerToken(clientAborted)
        ));

        AssertServiceLog(logger, clientAborted, "分页查询商品失败");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task 看板Executive报表服务_请求令牌已取消时只上抛不记错误_其他取消仍记错误(
        bool hourlyTraffic,
        bool clientAborted
    )
    {
        var adoToken = clientAborted ? _requestAborted.Token : _serverTimeout.Token;
        _localDb.Ado.CancellationToken = adoToken;
        _posmDb.Ado.CancellationToken = adoToken;
        var logger = new TestLogger<SalesDashboardReactService>();
        var service = new SalesDashboardReactService(
            CreateContext<SqlSugarContext>(_localDb),
            CreateContext<POSMSqlSugarContext>(_posmDb),
            Mock.Of<IMapper>(),
            logger,
            new MemoryCache(new MemoryCacheOptions()),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()
        );
        var range = new DateRangeDto
        {
            StartDate = new DateTime(2026, 9, 1),
            EndDate = new DateTime(2026, 9, 7),
            CompareMode = CompareMode.ByDate,
        };

        if (hourlyTraffic)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetExecutiveHourlyTrafficAsync(
                range,
                new List<string> { "S1" },
                CallerToken(clientAborted)
            ));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetExecutiveBranchPerformanceAsync(
                range,
                null,
                new List<string> { "S1" },
                CallerToken(clientAborted)
            ));
        }

        AssertServiceLog(
            logger,
            clientAborted,
            hourlyTraffic ? "GetExecutiveHourlyTrafficAsync failed" : "GetExecutiveBranchPerformanceAsync failed"
        );
    }

    [Fact]
    public async Task 浏览器扩展批量摘要_客户端中止时排名不再降级而是上抛()
    {
        var service = CreateBrowserExtensionService(_requestAborted.Token, out var logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetProductSummariesAsync(
            CreateSummaryRequest(),
            _requestAborted.Token
        ));

        Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
    }

    [Fact]
    public async Task 浏览器扩展批量摘要_服务端取消排名仍降级并记告警()
    {
        var service = CreateBrowserExtensionService(_serverTimeout.Token, out var logger);

        var response = await service.GetProductSummariesAsync(CreateSummaryRequest(), CancellationToken.None);

        // 排名失败只降级：采购摘要照常返回，排名标记为不可用。
        Assert.False(response.SalesRankingAvailable);
        Assert.Equal("A1", Assert.Single(response.Items).ItemNumber);
        var warning = Assert.Single(logger.Entries, entry => entry.LogLevel == LogLevel.Warning);
        Assert.IsAssignableFrom<OperationCanceledException>(warning.Exception);
        Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Error);
    }

    private BrowserExtensionService CreateBrowserExtensionService(
        CancellationToken rankingAdoToken,
        out TestLogger<BrowserExtensionService> logger
    )
    {
        _localDb.CodeFirst.InitTables(typeof(Store));
        _localDb.Insertable(new Store
        {
            StoreGUID = "store-guid-s001",
            StoreCode = "S001",
            StoreName = "测试分店",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommand();

        // 采购摘要是 SQL Server 专用语句，SQLite 下换成等价的空结果，让流程走到销量排名。
        _localDb.Aop.OnExecutingChangeSql = (sql, parameters) =>
            sql.Contains("RequestedItems", StringComparison.Ordinal)
                ? new KeyValuePair<string, SugarParameter[]>(
                    "SELECT 'X' AS ItemNumber WHERE 1 = 0",
                    Array.Empty<SugarParameter>()
                )
                : new KeyValuePair<string, SugarParameter[]>(sql, parameters);
        // 取消只注入到排名阶段首个查询（启用 POS 分店），采购摘要阶段不受影响。
        _posmDb.Ado.CancellationToken = rankingAdoToken;

        var options = new Mock<IOptionsSnapshot<BrowserExtensionOptions>>(MockBehavior.Strict);
        options.SetupGet(item => item.Value).Returns(new BrowserExtensionOptions());
        logger = new TestLogger<BrowserExtensionService>();
        return new BrowserExtensionService(
            CreateContext<SqlSugarContext>(_localDb),
            CreateContext<POSMSqlSugarContext>(_posmDb),
            options.Object,
            logger,
            new MemoryCache(new MemoryCacheOptions())
        );
    }

    private static BrowserExtensionProductSummaryBatchRequestDto CreateSummaryRequest() => new()
    {
        StoreCode = "S001",
        SupplierCode = "240",
        ItemNumbers = new List<string> { "A1" },
    };

    private CancellationToken CallerToken(bool clientAborted) =>
        clientAborted ? _requestAborted.Token : CancellationToken.None;

    private static void AssertServiceLog(ITestLoggerSink logger, bool clientAborted, string errorMessage)
    {
        if (clientAborted)
        {
            Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
            return;
        }

        var error = Assert.Single(logger.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Equal(errorMessage, error.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(error.Exception);
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = false,
        InitKeyType = InitKeyType.Attribute,
    };

    private static TContext CreateContext<TContext>(ISqlSugarClient db) where TContext : class
    {
        var context = (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));
        typeof(TContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _posmDb.Dispose();
        _localConnection.Dispose();
        _posmConnection.Dispose();
        _requestAborted.Dispose();
        _serverTimeout.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
    }
}
