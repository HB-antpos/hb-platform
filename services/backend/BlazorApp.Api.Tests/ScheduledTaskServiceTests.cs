using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ScheduledTaskServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly ScheduledTaskLogService _taskLogService;

    public ScheduledTaskServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        CreateScheduledTaskLogTable(_db);
        _taskLogService = new ScheduledTaskLogService(
            CreateSqlSugarContext(_db),
            NullLogger<ScheduledTaskLogService>.Instance
        );
    }

    [Fact]
    public async Task ExecuteHourlyTask_前序任务失败时_仍为每个子任务创建独立作用域并继续执行后续任务()
    {
        var dataSyncService = new Mock<IDataSyncReactService>(MockBehavior.Strict);
        dataSyncService
            .Setup(x => x.SyncPosmProductSupplierMappingsIncrementalAsync())
            .ThrowsAsync(new InvalidOperationException("sync boom"));

        var cacheWarmer = new Mock<IStoreOrderCacheWarmer>(MockBehavior.Strict);
        var cacheWarmExecuted = false;
        cacheWarmer
            .Setup(x => x.WarmUpHomePageAsync())
            .Callback(() => cacheWarmExecuted = true)
            .Returns(Task.CompletedTask);

        var scopeFactory = CreateScopeFactory(
            CreateScope(new Dictionary<Type, object?>()),
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(ScheduledTaskLogService)] = _taskLogService,
                    [typeof(IDataSyncReactService)] = dataSyncService.Object,
                }
            ),
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(ScheduledTaskLogService)] = _taskLogService,
                    [typeof(SalesStatisticsJobService)] = new InvalidOperationException("stats boom"),
                }
            ),
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(ScheduledTaskLogService)] = _taskLogService,
                    [typeof(IStoreOrderCacheWarmer)] = cacheWarmer.Object,
                }
            )
        );

        var service = CreateScheduledTaskService(scopeFactory.Object, NullLogger<ScheduledTaskService>.Instance);

        await InvokeExecuteHourlyTaskAsync(service);

        Assert.Equal(4, scopeFactory.Invocations.Count(x => x.Method.Name == nameof(IServiceScopeFactory.CreateScope)));
        Assert.True(cacheWarmExecuted);
        cacheWarmer.Verify(x => x.WarmUpHomePageAsync(), Times.Once);
    }

    [Fact]
    public async Task ExecuteHourlyTask_同步服务返回失败结果时_不应覆盖成成功日志()
    {
        var dataSyncService = new Mock<IDataSyncReactService>(MockBehavior.Strict);
        dataSyncService
            .Setup(x => x.SyncPosmProductSupplierMappingsIncrementalAsync())
            .ReturnsAsync(
                new SyncResult
                {
                    IsSuccess = false,
                    Message = "同步失败: 事务已完成",
                }
            );

        var cacheWarmer = new Mock<IStoreOrderCacheWarmer>(MockBehavior.Strict);
        cacheWarmer.Setup(x => x.WarmUpHomePageAsync()).Returns(Task.CompletedTask);

        var scopeFactory = CreateScopeFactory(
            CreateScope(new Dictionary<Type, object?>()),
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(ScheduledTaskLogService)] = _taskLogService,
                    [typeof(IDataSyncReactService)] = dataSyncService.Object,
                }
            ),
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(ScheduledTaskLogService)] = _taskLogService,
                    [typeof(SalesStatisticsJobService)] = new InvalidOperationException("stats boom"),
                }
            ),
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(ScheduledTaskLogService)] = _taskLogService,
                    [typeof(IStoreOrderCacheWarmer)] = cacheWarmer.Object,
                }
            )
        );

        var service = CreateScheduledTaskService(scopeFactory.Object, NullLogger<ScheduledTaskService>.Instance);

        await InvokeExecuteHourlyTaskAsync(service);

        var syncTaskLog = await _db.Queryable<ScheduledTaskLog>()
            .SingleAsync(x => x.TaskType == TaskType.SyncPosmProductSupplierMappingsIncremental);
        Assert.Equal(BlazorApp.Shared.Models.HBweb.TaskStatus.Failed, syncTaskLog.Status);
        Assert.Contains("事务已完成", syncTaskLog.ErrorMessage);
    }

    [Fact]
    public async Task TryLogTaskFailureAsync_记录失败日志再次异常时_应吞掉异常并写入错误日志()
    {
        var logger = new TestLogger<ScheduledTaskService>();
        var helper = typeof(ScheduledTaskService).GetMethod(
            "TryLogTaskFailureAsync",
            BindingFlags.Static | BindingFlags.NonPublic
        );

        Assert.NotNull(helper);

        var task = helper!.Invoke(
            null,
            new object[]
            {
                (Func<Task>)(() => throw new InvalidOperationException("log boom")),
                logger,
                "商品-供应商映射增量同步",
            }
        ) as Task;

        Assert.NotNull(task);
        await task!;

        Assert.Contains(
            logger.Entries,
            entry =>
                entry.LogLevel == LogLevel.Error
                && entry.Message.Contains("记录任务失败日志时发生异常", StringComparison.Ordinal)
                && entry.Message.Contains("商品-供应商映射增量同步", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task LogTaskSuccessStrictAsync_任务日志不存在时抛出异常()
    {
        var missingTaskId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _taskLogService.LogTaskSuccessStrictAsync(missingTaskId)
        );

        Assert.Contains(missingTaskId.ToString(), exception.Message);
    }

    [Fact]
    public async Task LogTaskSuccessStrictAsync_成功时持久化完成时间()
    {
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.UpdateCurrentHourStatistics,
            new TaskParameters()
        );

        await _taskLogService.LogTaskSuccessStrictAsync(taskLog.Id);

        var persisted = await _db.Queryable<ScheduledTaskLog>().SingleAsync(item => item.Id == taskLog.Id);
        Assert.Equal(BlazorApp.Shared.Models.HBweb.TaskStatus.Success, persisted.Status);
        Assert.NotNull(persisted.CompletedAt);
    }

    [Fact]
    public async Task LogTaskSkippedStrictAsync_跳过应持久化Skipped而非Success()
    {
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.UpdateCurrentHourStatistics,
            new TaskParameters()
        );

        await _taskLogService.LogTaskSkippedStrictAsync(taskLog.Id, "日期租约仍在运行");

        var persisted = await _db.Queryable<ScheduledTaskLog>().SingleAsync(item => item.Id == taskLog.Id);
        Assert.Equal(BlazorApp.Shared.Models.HBweb.TaskStatus.Skipped, persisted.Status);
        Assert.NotEqual(BlazorApp.Shared.Models.HBweb.TaskStatus.Success, persisted.Status);
        Assert.False(persisted.CanRetry);
        Assert.NotNull(persisted.CompletedAt);
        Assert.Contains("日期租约", persisted.ErrorMessage);
    }

    [Fact]
    public async Task LogTaskSuccessStrictAsync_成功写入失败后可严格标记Failed()
    {
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.UpdateCurrentHourStatistics,
            new TaskParameters()
        );
        _db.Ado.ExecuteCommand(
            """
            CREATE TRIGGER reject_success_status
            BEFORE UPDATE ON ScheduledTaskLog
            WHEN NEW.Status = 'Success'
            BEGIN
                SELECT RAISE(ABORT, 'reject success');
            END;
            """
        );

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _taskLogService.LogTaskSuccessStrictAsync(taskLog.Id)
        );
        await _taskLogService.LogTaskFailureStrictAsync(taskLog.Id, "成功版本写入失败");

        var persisted = await _db.Queryable<ScheduledTaskLog>().SingleAsync(item => item.Id == taskLog.Id);
        Assert.Equal(BlazorApp.Shared.Models.HBweb.TaskStatus.Failed, persisted.Status);
        Assert.Contains("成功版本写入失败", persisted.ErrorMessage);
    }

    [Fact]
    public async Task LogTaskSuccessAsync_更新返回False时不发布完成并保留性能运行映射()
    {
        PerformanceOperationalRunBridge.Configure(null);
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.UpdateCurrentHourStatistics,
            new TaskParameters()
        );
        var performanceRuns = GetPerformanceRuns();
        Assert.True(performanceRuns.ContainsKey(taskLog.Id));
        var deleted = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (deleted || !IsScheduledTaskLogUpdate(sql))
            {
                return;
            }
            deleted = true;
            using var concurrentDb = CreateIndependentDb();
            concurrentDb.Deleteable<ScheduledTaskLog>()
                .Where(item => item.Id == taskLog.Id)
                .ExecuteCommand();
        };

        try
        {
            await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            Assert.True(deleted);
            Assert.True(performanceRuns.ContainsKey(taskLog.Id));
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
            performanceRuns.TryRemove(taskLog.Id, out _);
        }
    }

    [Theory]
    [InlineData("success", BlazorApp.Shared.Models.HBweb.TaskStatus.Success)]
    [InlineData("success-strict", BlazorApp.Shared.Models.HBweb.TaskStatus.Success)]
    [InlineData("skipped", BlazorApp.Shared.Models.HBweb.TaskStatus.Skipped)]
    [InlineData("failed", BlazorApp.Shared.Models.HBweb.TaskStatus.Failed)]
    [InlineData("failed-strict", BlazorApp.Shared.Models.HBweb.TaskStatus.Failed)]
    public async Task 终态日志_请求ADO令牌已取消时_仍在独立终态Scope持久化(
        string terminalStatus,
        string expectedStatus)
    {
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.BatchFullRefreshConcurrent,
            new TaskParameters()
        );
        taskLog.CreatedBy = "manual-user";
        taskLog.UpdatedBy = "manual-user";
        taskLog.UpdatedAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.Updateable(taskLog).ExecuteCommandAsync();
        using var requestAbort = new CancellationTokenSource();
        requestAbort.Cancel();
        // 模拟 JWT 会话校验把 RequestAborted 写入当前 HTTP scoped SqlSugar ADO 后遗留的生产路径。
        _db.Ado.CancellationToken = requestAbort.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _db.Queryable<ScheduledTaskLog>().SingleAsync(item => item.Id == taskLog.Id)
        );

        using var terminalDb = CreateIndependentDb();
        var observedTerminalTokens = new List<CancellationToken?>();
        terminalDb.Aop.OnLogExecuting = (_, _) =>
        {
            observedTerminalTokens.Add(terminalDb.Ado.CancellationToken);
        };
        var terminalContext = CreateSqlSugarContext(terminalDb);
        var scopeFactory = CreateScopeFactory(
            CreateScope(
                new Dictionary<Type, object?>
                {
                    [typeof(SqlSugarContext)] = terminalContext,
                }
            )
        );
        var service = new ScheduledTaskLogService(
            CreateSqlSugarContext(_db),
            NullLogger<ScheduledTaskLogService>.Instance,
            scopeFactory.Object
        );

        try
        {
            switch (terminalStatus)
            {
                case "success":
                    await service.LogTaskSuccessAsync(taskLog.Id);
                    break;
                case "success-strict":
                    await service.LogTaskSuccessStrictAsync(taskLog.Id);
                    break;
                case "skipped":
                    await service.LogTaskSkippedStrictAsync(taskLog.Id, "日期已有其他执行者");
                    break;
                case "failed":
                    await service.LogTaskFailureAsync(taskLog.Id, "统计刷新失败");
                    break;
                case "failed-strict":
                    await service.LogTaskFailureStrictAsync(taskLog.Id, "统计刷新失败");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(terminalStatus));
            }

            var terminalTokensDuringWrite = observedTerminalTokens.ToArray();
            var persisted = await terminalDb.Queryable<ScheduledTaskLog>()
                .SingleAsync(item => item.Id == taskLog.Id);
            Assert.Equal(expectedStatus, persisted.Status);
            Assert.NotNull(persisted.CompletedAt);
            Assert.Equal("manual-user", persisted.CreatedBy);
            Assert.Equal("manual-user", persisted.UpdatedBy);
            Assert.Equal(persisted.CompletedAt, persisted.UpdatedAt);
            Assert.True(_db.Ado.CancellationToken?.IsCancellationRequested);
            Assert.NotEmpty(terminalTokensDuringWrite);
            Assert.All(terminalTokensDuringWrite, token =>
            {
                Assert.True(token.HasValue);
                Assert.True(token.Value.CanBeCanceled);
                Assert.False(token.Value.IsCancellationRequested);
            });
            Assert.Null(terminalDb.Ado.CancellationToken);
        }
        finally
        {
            _db.Ado.RemoveCancellationToken();
        }
    }

    [Fact]
    public async Task 严格终态日志_独立Deadline取消SQL时_等待收尾且不继承请求AsyncLocal()
    {
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.BatchFullRefreshConcurrent,
            new TaskParameters()
        );
        using var terminalDb = CreateIndependentDb();
        var terminalContext = CreateSqlSugarContext(terminalDb);
        var requestFlow = new AsyncLocal<string?> { Value = "request-flow" };
        var observedWorkerFlow = new List<string?>();
        var updateSawCancelledDeadline = false;
        var updateCommands = 0;
        terminalDb.Aop.OnLogExecuting = (sql, _) =>
        {
            observedWorkerFlow.Add(requestFlow.Value);
            if (!IsScheduledTaskLogUpdate(sql))
            {
                return;
            }

            updateCommands++;
            // 在真正执行 UPDATE 前等待独立 ADO deadline 到期；随后由 SqlSugar 将已取消令牌传给 DbCommand。
            // 250ms deadline 由生产代码的计时器触发，这里只是等它被线程池调度到，属于防挂死预算。
            updateSawCancelledDeadline = SpinWait.SpinUntil(
                () => terminalDb.Ado.CancellationToken?.IsCancellationRequested == true,
                AsyncTestWaitSupport.DefaultTimeout
            );
        };

        var provider = new Mock<IServiceProvider>(MockBehavior.Strict);
        provider
            .Setup(x => x.GetService(typeof(SqlSugarContext)))
            .Returns(terminalContext);
        var scope = new Mock<IServiceScope>(MockBehavior.Strict);
        scope.SetupGet(x => x.ServiceProvider).Returns(provider.Object);
        scope.Setup(x => x.Dispose());
        var scopeFactory = CreateScopeFactory(scope.Object);
        var service = new ScheduledTaskLogService(
            CreateSqlSugarContext(_db),
            NullLogger<ScheduledTaskLogService>.Instance,
            scopeFactory.Object,
            TimeSpan.FromMilliseconds(250)
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LogTaskSuccessStrictAsync(taskLog.Id)
        );

        // PersistTerminalAsync 等待 Task.Run 收尾，异常返回时 scope 已释放且没有遗留后台 UPDATE。
        scope.Verify(x => x.Dispose(), Times.Once);
        Assert.True(updateSawCancelledDeadline);
        Assert.Equal(1, updateCommands);
        Assert.All(observedWorkerFlow, flow => Assert.Null(flow));
        Assert.Equal("request-flow", requestFlow.Value);
        Assert.Null(terminalDb.Ado.CancellationToken);

        await Task.Delay(100);
        Assert.Equal(1, updateCommands);
        var persisted = await terminalDb.Queryable<ScheduledTaskLog>()
            .SingleAsync(item => item.Id == taskLog.Id);
        Assert.Equal(BlazorApp.Shared.Models.HBweb.TaskStatus.Running, persisted.Status);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal("request-flow", requestFlow.Value);
    }

    [Fact]
    public async Task LogTaskFailureAsync_更新返回False时不发布完成并保留性能运行映射()
    {
        PerformanceOperationalRunBridge.Configure(null);
        var taskLog = await _taskLogService.LogTaskStartAsync(
            TaskType.UpdateCurrentHourStatistics,
            new TaskParameters()
        );
        var performanceRuns = GetPerformanceRuns();
        Assert.True(performanceRuns.ContainsKey(taskLog.Id));
        var deleted = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (deleted || !IsScheduledTaskLogUpdate(sql))
            {
                return;
            }
            deleted = true;
            using var concurrentDb = CreateIndependentDb();
            concurrentDb.Deleteable<ScheduledTaskLog>()
                .Where(item => item.Id == taskLog.Id)
                .ExecuteCommand();
        };

        try
        {
            await _taskLogService.LogTaskFailureAsync(taskLog.Id, "test failure");
            Assert.True(deleted);
            Assert.True(performanceRuns.ContainsKey(taskLog.Id));
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
            performanceRuns.TryRemove(taskLog.Id, out _);
        }
    }

    [Fact]
    public async Task StopAsync_与重新布置并发时原子清空销售统计Timer引用()
    {
        var service = CreateScheduledTaskService(
            CreateScopeFactory(CreateScope(new Dictionary<Type, object?>())).Object,
            NullLogger<ScheduledTaskService>.Instance
        );
        var timerField = typeof(ScheduledTaskService).GetField(
            "_salesStatisticsTimer",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        timerField!.SetValue(
            service,
            new Timer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
        );
        var scheduleMethod = typeof(ScheduledTaskService).GetMethod(
            "ScheduleNextSalesStatisticsRun",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var rearmTask = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                scheduleMethod!.Invoke(service, null);
            }
        });

        await Task.WhenAll(rearmTask, service.StopAsync(CancellationToken.None));

        Assert.Null(timerField.GetValue(service));
    }

    [Fact]
    public void CalculateNextMonthlyRun_默认31号时_二月取月末()
    {
        var service = CreateScheduledTaskService(
            CreateScopeFactory(CreateScope(new Dictionary<Type, object?>())).Object,
            NullLogger<ScheduledTaskService>.Instance
        );
        var method = typeof(ScheduledTaskService).GetMethod(
            "CalculateNextMonthlyRun",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var now = new DateTime(2026, 2, 27, 12, 0, 0);

        var delay = (TimeSpan)method!.Invoke(service, new object[] { now })!;

        Assert.Equal(new DateTime(2026, 2, 28, 23, 59, 0), now.Add(delay));
    }

    [Fact]
    public void CalculateNextDailyRun_默认二十三点和正负五分钟抖动始终落在二十二至六点窗口()
    {
        var options = new ScheduledTaskOptions();
        var service = CreateScheduledTaskService(
            CreateScopeFactory(CreateScope(new Dictionary<Type, object?>())).Object,
            NullLogger<ScheduledTaskService>.Instance,
            options
        );
        var method = typeof(ScheduledTaskService).GetMethod(
            "CalculateNextDailyRun",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var now = new DateTime(2026, 9, 8, 12, 0, 0);

        Assert.Equal(23, options.DailyTaskHour);
        Assert.True(options.EnableJitter);
        Assert.Equal(5, options.JitterMaxMinutes);
        for (var index = 0; index < 200; index++)
        {
            var delay = (TimeSpan)method!.Invoke(service, new object[] { now })!;
            var next = now.Add(delay);
            Assert.True(
                next.TimeOfDay >= TimeSpan.FromHours(22)
                    || next.TimeOfDay < TimeSpan.FromHours(6),
                $"下一次每日任务落在窗口外: {next:yyyy-MM-dd HH:mm:ss}"
            );
        }
    }

    [Fact]
    public void CalculateNextWeeklyRun_默认Jitter开启时_不跨到下周()
    {
        var service = CreateScheduledTaskService(
            CreateScopeFactory(CreateScope(new Dictionary<Type, object?>())).Object,
            NullLogger<ScheduledTaskService>.Instance
        );
        var method = typeof(ScheduledTaskService).GetMethod(
            "CalculateNextWeeklyRun",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var now = new DateTime(2026, 7, 5, 23, 58, 0);

        var delay = (TimeSpan)method!.Invoke(service, new object[] { now })!;

        Assert.Equal(new DateTime(2026, 7, 5, 23, 59, 0), now.Add(delay));
    }

    [Theory]
    [InlineData("2026-07-10 10:12:34", "2026-07-10 10:30:00")]
    [InlineData("2026-07-10 10:30:00", "2026-07-10 11:00:00")]
    [InlineData("2026-07-10 23:59:59", "2026-07-11 00:00:00")]
    public void CalculateNextSalesStatisticsRun_始终对齐下一个整点或半点(
        string nowText,
        string expectedText
    )
    {
        var method = typeof(ScheduledTaskService).GetMethod(
            "CalculateNextSalesStatisticsRun",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        var now = DateTime.Parse(nowText);

        Assert.NotNull(method);
        var delay = (TimeSpan)method!.Invoke(null, new object[] { now })!;

        Assert.Equal(DateTime.Parse(expectedText), now.Add(delay));
    }

    [Fact]
    public void GetCurrentWeekMonday_周日执行时_返回刚结束本周周一()
    {
        var method = typeof(ScheduledTaskService).GetMethod(
            "GetCurrentWeekMonday",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        var sunday = new DateTime(2026, 7, 5, 23, 59, 0);

        var monday = (DateTime)method!.Invoke(null, new object[] { sunday })!;

        Assert.Equal(new DateTime(2026, 6, 29), monday);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private SqlSugarClient CreateIndependentDb() =>
        new(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

    private static ConcurrentDictionary<Guid, (string ExternalRunId, int Attempt)> GetPerformanceRuns() =>
        (ConcurrentDictionary<Guid, (string ExternalRunId, int Attempt)>)
            typeof(ScheduledTaskLogService)
                .GetField("PerformanceRuns", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

    private static bool IsScheduledTaskLogUpdate(string sql) =>
        sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("ScheduledTaskLog", StringComparison.OrdinalIgnoreCase);

    private static void CreateScheduledTaskLogTable(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand(
            """
            CREATE TABLE IF NOT EXISTS ScheduledTaskLog (
                Id TEXT PRIMARY KEY,
                TaskType TEXT NOT NULL,
                TaskParameters TEXT NULL,
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                DurationMs INTEGER NULL,
                ErrorMessage TEXT NULL,
                RetryCount INTEGER NOT NULL,
                CanRetry INTEGER NOT NULL,
                ScheduledTime TEXT NOT NULL,
                TriggeredBy TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CreatedBy TEXT NULL,
                UpdatedAt TEXT NULL,
                UpdatedBy TEXT NULL,
                IsDeleted INTEGER NULL
            );
            """
        );
    }

    private static ScheduledTaskService CreateScheduledTaskService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledTaskService> logger,
        ScheduledTaskOptions? options = null
    )
    {
        var service = (ScheduledTaskService)RuntimeHelpers.GetUninitializedObject(
            typeof(ScheduledTaskService)
        );

        typeof(ScheduledTaskService)
            .GetField("_scopeFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, scopeFactory);
        typeof(ScheduledTaskService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, logger);
        typeof(ScheduledTaskService)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, options ?? new ScheduledTaskOptions());
        typeof(ScheduledTaskService)
            .GetField("_sydneyTimeZone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, CreateAllowedTimeZone());
        typeof(ScheduledTaskService)
            .GetField("_salesStatisticsTimerLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new object());

        return service;
    }

    private static async Task InvokeExecuteHourlyTaskAsync(ScheduledTaskService service)
    {
        var method = typeof(ScheduledTaskService).GetMethod(
            "ExecuteHourlyTask",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var task = method!.Invoke(service, null) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static TimeZoneInfo CreateAllowedTimeZone()
    {
        var targetHour = 10;
        var currentHour = DateTime.UtcNow.Hour;
        var offsetHours = targetHour - currentHour;

        if (offsetHours < -12)
        {
            offsetHours += 24;
        }
        else if (offsetHours > 14)
        {
            offsetHours -= 24;
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            $"TestZone_{offsetHours}",
            TimeSpan.FromHours(offsetHours),
            "TestZone",
            "TestZone"
        );
    }

    private static Mock<IServiceScopeFactory> CreateScopeFactory(params IServiceScope[] scopes)
    {
        var queue = new Queue<IServiceScope>(scopes);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopeFactory.Setup(x => x.CreateScope()).Returns(() => queue.Dequeue());
        return scopeFactory;
    }

    private static IServiceScope CreateScope(Dictionary<Type, object?> services)
    {
        var provider = new Mock<IServiceProvider>(MockBehavior.Strict);
        provider
            .Setup(x => x.GetService(It.IsAny<Type>()))
            .Returns<Type>(serviceType =>
            {
                if (!services.TryGetValue(serviceType, out var service))
                {
                    return null;
                }

                if (service is Exception exception)
                {
                    throw exception;
                }

                return service;
            });

        var scope = new Mock<IServiceScope>(MockBehavior.Strict);
        scope.SetupGet(x => x.ServiceProvider).Returns(provider.Object);
        scope.Setup(x => x.Dispose());
        return scope.Object;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }
}
