using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;
using TaskStatus = BlazorApp.Shared.Models.HBweb.TaskStatus;
using TaskTrigger = BlazorApp.Shared.Models.HBweb.TaskTrigger;
using TaskType = BlazorApp.Shared.Models.HBweb.TaskType;

namespace BlazorApp.Api.Tests;

public sealed class SalesStatisticsAlignmentBackgroundRecalculateServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly SqlSugarContext _context;
    private readonly ScheduledTaskLogService _taskLogService;

    public SalesStatisticsAlignmentBackgroundRecalculateServiceTests()
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
        _context = CreateSqlSugarContext(_db);
        _taskLogService = new ScheduledTaskLogService(
            _context,
            NullLogger<ScheduledTaskLogService>.Instance
        );
    }

    [Fact]
    public async Task QueueAsync_有效日期_立即返回JobId并创建Running日志()
    {
        var recalculateCompletion = new TaskCompletionSource<DailyStatisticsAlignmentRecalculateResponseDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var alignmentService = CreateAlignmentServiceMock(
            (dates, _) =>
            {
                Assert.Equal(new[] { new DateTime(2026, 7, 6), new DateTime(2026, 7, 7) }, dates.ToArray());
                return recalculateCompletion.Task;
            }
        );
        var cacheWarmer = new Mock<ISalesDashboardCacheWarmer>();
        cacheWarmer.Setup(x => x.ClearCacheAsync()).Returns(Task.CompletedTask);
        using var provider = CreateServiceProvider(alignmentService.Object, cacheWarmer.Object);
        var service = CreateService(provider);

        var result = await service.QueueAsync(
            new[] { new DateTime(2026, 7, 7), new DateTime(2026, 7, 6), new DateTime(2026, 7, 6) },
            3
        );

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.JobId);
        Assert.Contains("已提交 2 天异常统计后台补算", result.Message);
        Assert.Empty(result.ProcessedDates);
        Assert.Empty(result.SkippedDates);
        Assert.Empty(result.FailedDates);

        var runningLog = await _db.Queryable<ScheduledTaskLog>().SingleAsync(x => x.Id == result.JobId);
        Assert.NotNull(runningLog);
        Assert.Equal(TaskType.RecalculateDailyStatisticsAlignment, runningLog.TaskType);
        Assert.Equal(TaskStatus.Running, runningLog.Status);
        Assert.Equal(TaskTrigger.Manual, runningLog.TriggeredBy);
        Assert.False(runningLog.CanRetry);
        Assert.Contains("\"StartDate\":\"2026-07-06\"", runningLog.TaskParameters);
        Assert.Contains("\"EndDate\":\"2026-07-07\"", runningLog.TaskParameters);
        Assert.Contains("\"MaxConcurrency\":3", runningLog.TaskParameters);
        Assert.Contains("\"dates\":[\"2026-07-06\",\"2026-07-07\"]", runningLog.TaskParameters);

        recalculateCompletion.SetResult(new DailyStatisticsAlignmentRecalculateResponseDto
        {
            JobId = Guid.NewGuid(),
            Success = true,
            Message = "已补算 2 天",
            ProcessedDates = new List<DateTime>
            {
                new(2026, 7, 6),
                new(2026, 7, 7),
            },
        });

        var successLog = await WaitForStatusAsync(result.JobId, TaskStatus.Success);
        Assert.Equal(TaskStatus.Success, successLog.Status);
        cacheWarmer.Verify(x => x.ClearCacheAsync(), Times.Once);
    }

    [Fact]
    public async Task QueueAsync_后台补算失败时清缓存并记录Failed()
    {
        var failedDate = new DateTime(2026, 7, 6);
        var alignmentService = CreateAlignmentServiceMock((_, _) => Task.FromResult(
            new DailyStatisticsAlignmentRecalculateResponseDto
            {
                JobId = Guid.NewGuid(),
                Success = false,
                Message = "已补算 0 天，失败 1 天",
                FailedDates = new List<DateTime> { failedDate },
            }
        ));
        var cacheWarmer = new Mock<ISalesDashboardCacheWarmer>();
        cacheWarmer.Setup(x => x.ClearCacheAsync()).Returns(Task.CompletedTask);
        using var provider = CreateServiceProvider(alignmentService.Object, cacheWarmer.Object);
        var service = CreateService(provider);

        var result = await service.QueueAsync(new[] { failedDate }, 3);

        var failedLog = await WaitForStatusAsync(result.JobId, TaskStatus.Failed);
        Assert.Equal(TaskStatus.Failed, failedLog.Status);
        Assert.False(failedLog.CanRetry);
        Assert.Contains("已补算 0 天，失败 1 天", failedLog.ErrorMessage);
        Assert.Contains("2026-07-06", failedLog.ErrorMessage);
        cacheWarmer.Verify(x => x.ClearCacheAsync(), Times.Once);
    }

    [Fact]
    public async Task QueueAsync_后台补算部分完成且失败时清缓存并记录Failed()
    {
        var processedDate = new DateTime(2026, 7, 6);
        var failedDate = processedDate.AddDays(1);
        var alignmentService = CreateAlignmentServiceMock((_, _) => Task.FromResult(
            new DailyStatisticsAlignmentRecalculateResponseDto
            {
                JobId = Guid.NewGuid(),
                Success = false,
                Message = "已补算 1 天，失败 1 天",
                ProcessedDates = new List<DateTime> { processedDate },
                FailedDates = new List<DateTime> { failedDate },
            }
        ));
        var cacheWarmer = new Mock<ISalesDashboardCacheWarmer>();
        cacheWarmer.Setup(x => x.ClearCacheAsync()).Returns(Task.CompletedTask);
        using var provider = CreateServiceProvider(alignmentService.Object, cacheWarmer.Object);
        var service = CreateService(provider);

        var result = await service.QueueAsync(new[] { processedDate, failedDate }, 3);

        var failedLog = await WaitForStatusAsync(result.JobId, TaskStatus.Failed);
        Assert.Contains("失败 1 天", failedLog.ErrorMessage);
        cacheWarmer.Verify(x => x.ClearCacheAsync(), Times.Once);
    }

    [Fact]
    public async Task RecalculateDailyStatisticsAlignment_有效日期_立即返回JobId并创建Running日志()
    {
        var recalculateCompletion = new TaskCompletionSource<DailyStatisticsAlignmentRecalculateResponseDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var statisticsJobService = (SalesStatisticsJobService)RuntimeHelpers.GetUninitializedObject(
            typeof(SalesStatisticsJobService)
        );
        var alignmentService = CreateAlignmentServiceMock((_, _) => recalculateCompletion.Task);
        var cacheWarmer = new Mock<ISalesDashboardCacheWarmer>();
        cacheWarmer.Setup(x => x.ClearCacheAsync()).Returns(Task.CompletedTask);
        using var provider = CreateServiceProvider(alignmentService.Object, cacheWarmer.Object);
        var controller = new StatisticsJobTriggerController(
            statisticsJobService,
            _taskLogService,
            _context,
            NullLogger<StatisticsJobTriggerController>.Instance,
            Mock.Of<IProductStoreDailyStatisticQueueService>(),
            alignmentService.Object,
            CreateService(provider)
        );

        var response = await controller.RecalculateDailyStatisticsAlignment(
            new DailyStatisticsAlignmentRecalculateRequest
            {
                Dates = new List<DateTime> { new(2026, 7, 6), new(2026, 7, 7) },
                MaxConcurrency = 4,
            }
        );

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.True(ReadAnonymousProperty<bool>(ok.Value, "success"));
        var jobId = ReadAnonymousProperty<Guid>(ok.Value, "jobId");
        Assert.NotEqual(Guid.Empty, jobId);
        Assert.Contains("已提交 2 天异常统计后台补算", ReadAnonymousProperty<string>(ok.Value, "message"));

        var runningLog = await _db.Queryable<ScheduledTaskLog>().SingleAsync(x => x.Id == jobId);
        Assert.Equal(TaskStatus.Running, runningLog.Status);
        Assert.Equal(TaskType.RecalculateDailyStatisticsAlignment, runningLog.TaskType);
        Assert.Contains("\"MaxConcurrency\":4", runningLog.TaskParameters);

        recalculateCompletion.SetResult(new DailyStatisticsAlignmentRecalculateResponseDto
        {
            JobId = Guid.NewGuid(),
            Success = true,
            Message = "已补算 2 天",
        });
        var successLog = await WaitForStatusAsync(jobId, TaskStatus.Success);
        Assert.Equal(TaskStatus.Success, successLog.Status);
    }

    [Fact]
    public async Task RecalculateDailyStatisticsAlignment_无日期_仍返回BadRequest且不创建日志()
    {
        var statisticsJobService = (SalesStatisticsJobService)RuntimeHelpers.GetUninitializedObject(
            typeof(SalesStatisticsJobService)
        );
        var alignmentService = CreateAlignmentServiceMock((_, _) =>
            throw new InvalidOperationException("空日期不应提交后台补算")
        );
        using var provider = CreateServiceProvider(
            alignmentService.Object,
            Mock.Of<ISalesDashboardCacheWarmer>()
        );
        var controller = new StatisticsJobTriggerController(
            statisticsJobService,
            _taskLogService,
            _context,
            NullLogger<StatisticsJobTriggerController>.Instance,
            Mock.Of<IProductStoreDailyStatisticQueueService>(),
            alignmentService.Object,
            CreateService(provider)
        );

        var response = await controller.RecalculateDailyStatisticsAlignment(
            new DailyStatisticsAlignmentRecalculateRequest { Dates = new List<DateTime>() }
        );

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains("请选择需要补算的日期", badRequest.Value!.ToString());
        var logCount = await _db.Queryable<ScheduledTaskLog>().CountAsync();
        Assert.Equal(0, logCount);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private SalesStatisticsAlignmentBackgroundRecalculateService CreateService(
        IServiceProvider serviceProvider
    )
    {
        return new SalesStatisticsAlignmentBackgroundRecalculateService(
            _taskLogService,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SalesStatisticsAlignmentBackgroundRecalculateService>.Instance
        );
    }

    private ServiceProvider CreateServiceProvider(
        SalesStatisticsAlignmentService alignmentService,
        ISalesDashboardCacheWarmer cacheWarmer
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _taskLogService);
        services.AddScoped(_ => alignmentService);
        services.AddScoped(_ => cacheWarmer);
        return services.BuildServiceProvider();
    }

    private static Mock<SalesStatisticsAlignmentService> CreateAlignmentServiceMock(
        Func<IEnumerable<DateTime>, int, Task<DailyStatisticsAlignmentRecalculateResponseDto>> handler
    )
    {
        var mock = new Mock<SalesStatisticsAlignmentService>(
            null!,
            null!,
            null!,
            null!,
            NullLogger<SalesStatisticsAlignmentService>.Instance
        );
        mock
            .Setup(x => x.RecalculateAsync(It.IsAny<IEnumerable<DateTime>>(), It.IsAny<int>()))
            .Returns((IEnumerable<DateTime> dates, int maxConcurrency) => handler(dates, maxConcurrency));
        return mock;
    }

    private async Task<ScheduledTaskLog> WaitForStatusAsync(Guid taskId, string expectedStatus)
    {
        var taskLog = await WaitForValueAsync(
            () => _db.Queryable<ScheduledTaskLog>().SingleAsync(x => x.Id == taskId),
            current => current?.Status == expectedStatus,
            describeLast: current =>
                $"等待任务状态 {expectedStatus}，当前状态：{current?.Status ?? "未找到"}"
        );
        return taskLog!;
    }

    private static T ReadAnonymousProperty<T>(object? value, string propertyName)
    {
        Assert.NotNull(value);
        var property = value!.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var propertyValue = property!.GetValue(value);
        return Assert.IsType<T>(propertyValue);
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

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
}
