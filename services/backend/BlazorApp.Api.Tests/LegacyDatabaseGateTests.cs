using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Orders.Infrastructure;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 旧库断开的关键保证：不连旧服务器、不再入队、历史统计不被覆盖、节日报表不重复计算 POSM。
/// </summary>
public sealed class LegacyDatabaseGateTests : IDisposable
{
    private const string RealHost = "real-legacy-host.example";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"legacy-gate-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;

    public LegacyDatabaseGateTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
    }

    [Fact]
    public void 未配置开关时按已断开处理()
    {
        // 只测解析结果：Configure 会改全局开关，影响并行运行的其他测试。
        Assert.Equal((false, false), LegacyDatabaseGate.ReadSwitches(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void 断开后三个上下文都不使用真实连接串_且缺连接串也能构造()
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hqEnabled: false, hbSalesEnabled: false);
        var empty = new ConfigurationBuilder().Build();

        var hq = new HqSqlSugarContext(empty, Mock.Of<ICurrentUserService>());
        using var concurrent = HqSqlSugarContext.CreateConcurrentConnection(empty);
        var hbSales = new HBSalesSqlSugarContext(empty);
        var hbSalesRecord = new HBSalesRecordSqlSugarContext(empty);

        Assert.Contains("127.0.0.1,1", hq.Db.CurrentConnectionConfig.ConnectionString);
        Assert.Contains("127.0.0.1,1", concurrent.CurrentConnectionConfig.ConnectionString);
        Assert.Contains("127.0.0.1,1", hbSales.Db.CurrentConnectionConfig.ConnectionString);
        Assert.Contains("127.0.0.1,1", hbSalesRecord.Db.CurrentConnectionConfig.ConnectionString);
    }

    [Fact]
    public void 断开后即使配置了真实连接串也不使用()
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hqEnabled: false, hbSalesEnabled: false);
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StoreHzgHQConnection"] = $"Server={RealHost};Database=HQ",
                ["ConnectionStrings:HBSalesConnection"] = $"Server={RealHost};Database=CPT",
                ["ConnectionStrings:HBSalesRecord"] = $"Server={RealHost};Database=POS",
            })
            .Build();

        Assert.DoesNotContain(
            RealHost,
            new HqSqlSugarContext(configured, Mock.Of<ICurrentUserService>()).Db.CurrentConnectionConfig.ConnectionString);
        Assert.DoesNotContain(RealHost, new HBSalesSqlSugarContext(configured).Db.CurrentConnectionConfig.ConnectionString);
        Assert.DoesNotContain(RealHost, new HBSalesRecordSqlSugarContext(configured).Db.CurrentConnectionConfig.ConnectionString);
    }

    [Fact]
    public void 开关打开时仍用配置的连接串()
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hqEnabled: true, hbSalesEnabled: true);
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HBSalesRecord"] = $"Server={RealHost};Database=POS",
            })
            .Build();

        Assert.Contains(RealHost, new HBSalesRecordSqlSugarContext(configured).Db.CurrentConnectionConfig.ConnectionString);
    }

    [Theory]
    [InlineData(LegacyDatabaseKind.Hq, false, true, true)]
    [InlineData(LegacyDatabaseKind.HBSales, true, false, true)]
    [InlineData(LegacyDatabaseKind.Hq, true, false, false)]
    [InlineData(LegacyDatabaseKind.HBSales, false, true, false)]
    public void 过滤器只拦截已断开的库(LegacyDatabaseKind kind, bool hq, bool hbSales, bool blocked)
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hq, hbSales);
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object()
        );

        new LegacyDatabaseDisabledAttribute(kind).OnActionExecuting(context);

        if (blocked)
            Assert.Equal(StatusCodes.Status410Gone, Assert.IsType<ObjectResult>(context.Result).StatusCode);
        else
            Assert.Null(context.Result);
    }

    [Fact]
    public async Task 断开后商品维护不再写入HQ队列()
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hqEnabled: false, hbSalesEnabled: false);
        var queue = new Mock<IProductHqSyncOutboxQueue>(MockBehavior.Strict);
        var writer = new ProductMaintenanceHqProjectionWriter(
            (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext)),
            (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext)),
            queue.Object,
            Mock.Of<IProductHqSyncService>(),
            NullLogger<ProductMaintenanceHqProjectionWriter>.Instance
        );

        var result = await writer.EnqueueAsync(
            Mock.Of<ISqlSugarClient>(),
            new ProductMaintenanceHqMutationRequest { ProductCode = "A", Source = "test", OperationKind = "x" }
        );

        Assert.Null(result);
        queue.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("2024-09-14", true)]
    [InlineData("2026-04-30", true)]
    [InlineData("2026-05-01", false)]
    [InlineData("2024-09-13", false)]
    public void 断开后只冻结历史窗口内的日期(string date, bool frozen)
    {
        var day = DateTime.Parse(date);
        using (LegacyDatabaseGate.OverrideForTests(hqEnabled: true, hbSalesEnabled: false))
        {
            Assert.Equal(frozen, SalesStatisticsHBSalesHistoryWindow.IsFrozen(day));
        }
        using (LegacyDatabaseGate.OverrideForTests(hqEnabled: true, hbSalesEnabled: true))
        {
            Assert.False(SalesStatisticsHBSalesHistoryWindow.IsFrozen(day));
        }
    }

    [Fact]
    public void 外购客户快照完整且不区分大小写()
    {
        Assert.Equal(23, StoreOrderExternalCustomerSnapshot.NamesByGuid.Count);
        Assert.Equal(
            "WAREHOUSE",
            StoreOrderExternalCustomerSnapshot.NamesByGuid["7ae36d06-d41c-4de1-a2e2-d8e9544452f7"]);
    }

    [Fact]
    public async Task 节日报表断开后窗口内读日统计_窗口外的行不计入()
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hqEnabled: false, hbSalesEnabled: false);
        _db.CodeFirst.InitTables<ProductStoreDailySalesStatistic>();
        await _db.Insertable(new[]
        {
            NewStatistic(new DateTime(2025, 12, 20), 7, 70m),
            NewStatistic(new DateTime(2026, 5, 1), 100, 1000m),
        }).ExecuteCommandAsync();
        var service = CreateHolidayService();

        var rows = await InvokeSalesAsync(
            service,
            "GetHBSalesDataAsync",
            new DateTime(2025, 12, 1),
            new DateTime(2026, 5, 31)
        );

        var row = Assert.Single(rows.Cast<object>());
        Assert.Equal(7, (int)row.GetType().GetProperty("Quantity")!.GetValue(row)!);
    }

    [Fact]
    public async Task 节日报表断开后窗口内不再读POSM避免重复计算()
    {
        using var _ = LegacyDatabaseGate.OverrideForTests(hqEnabled: false, hbSalesEnabled: false);
        var logger = new Mock<ILogger<HolidayProductReactService>>();
        var service = CreateHolidayService(logger.Object);

        // _posmContext 未注入。若没有提前返回，读 POSM 会抛空引用并写错误日志。
        var rows = await InvokeSalesAsync(
            service,
            "GetPOSMSalesDataAsync",
            new DateTime(2025, 12, 1),
            new DateTime(2025, 12, 31)
        );

        Assert.Empty(rows.Cast<object>());
        logger.Verify(
            item => item.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ),
            Times.Never
        );
    }

    private HolidayProductReactService CreateHolidayService(ILogger<HolidayProductReactService>? logger = null)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, _db);
        var service = (HolidayProductReactService)RuntimeHelpers.GetUninitializedObject(
            typeof(HolidayProductReactService));
        typeof(HolidayProductReactService)
            .GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        typeof(HolidayProductReactService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, logger ?? NullLogger<HolidayProductReactService>.Instance);
        return service;
    }

    private static async Task<System.Collections.IEnumerable> InvokeSalesAsync(
        HolidayProductReactService service,
        string method,
        DateTime start,
        DateTime end)
    {
        var target = typeof(HolidayProductReactService).GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic,
            new[] { typeof(List<string>), typeof(List<string>), typeof(DateTime), typeof(DateTime) }
        )!;
        var task = (Task)target.Invoke(
            service,
            new object[] { new List<string> { "P1" }, new List<string> { "S1" }, start, end }
        )!;
        await task;
        return (System.Collections.IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static ProductStoreDailySalesStatistic NewStatistic(DateTime date, int quantity, decimal amount) => new()
    {
        Date = date,
        BranchCode = "S1",
        SupplierCode = "SUP",
        ProductCode = "P1",
        TotalQuantity = quantity,
        TotalAmount = amount,
    };

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
