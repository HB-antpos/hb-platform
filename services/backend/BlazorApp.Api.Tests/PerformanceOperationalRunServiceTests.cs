using BlazorApp.Api.Services.Performance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PerformanceOperationalRunServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"performance-runs-{Guid.NewGuid():N}.db"
    );
    private readonly SqlSugarClient _db;

    public PerformanceOperationalRunServiceTests()
    {
        _db = CreateDb();
        _db.CodeFirst.InitTables<
            PerformanceOperationalRun,
            PerformanceOperationalRunTransitionOutbox
        >();
    }

    [Fact]
    public async Task 状态转换_先写入持久Outbox且进程重启后可继续应用()
    {
        var occurredAt = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var transition = PerformanceOperationalRunTransition.Started(
            "restart-safe-job",
            "hq",
            "products",
            occurredAt,
            3
        );

        var outboxId = await PerformanceOperationalRunWriterService.EnqueueAsync(
            _db,
            transition,
            occurredAt
        );

        Assert.Equal(1, await _db.Queryable<PerformanceOperationalRunTransitionOutbox>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PerformanceOperationalRun>().CountAsync());

        using var restartedDb = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        var processed = await PerformanceOperationalRunWriterService.ProcessOutboxAsync(
            restartedDb,
            outboxId,
            Options.Create(new PerformanceMetricsOptions()),
            "restarted-instance",
            occurredAt.AddSeconds(1)
        );

        Assert.True(processed);
        Assert.Equal(
            0,
            await restartedDb.Queryable<PerformanceOperationalRunTransitionOutbox>().CountAsync()
        );
        var run = await restartedDb.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("running", run.Status);
        Assert.Equal("restart-safe-job", run.ExternalRunId);
        Assert.Equal(3, run.Backlog);
    }

    [Fact]
    public async Task Outbox失败达到上限后保留死信而不是静默删除()
    {
        var now = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var outboxId = await PerformanceOperationalRunWriterService.EnqueueAsync(
            _db,
            PerformanceOperationalRunTransition.Queued(
                "poison-job",
                "background",
                "statistics",
                now
            ),
            now
        );

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await PerformanceOperationalRunWriterService.RecordOutboxFailureAsync(
                _db,
                outboxId,
                now.AddMinutes(attempt),
                new InvalidOperationException("test failure")
            );
        }

        var outbox = await _db
            .Queryable<PerformanceOperationalRunTransitionOutbox>()
            .SingleAsync();
        Assert.Equal(20, outbox.RetryCount);
        Assert.NotNull(outbox.DeadLetteredAtUtc);
        Assert.Equal(nameof(InvalidOperationException), outbox.LastErrorType);
    }

    [Fact]
    public async Task Outbox并发失败_重试计数原子累加且最多进入一次死信()
    {
        var now = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var outboxId = await PerformanceOperationalRunWriterService.EnqueueAsync(
            _db,
            PerformanceOperationalRunTransition.Queued(
                "concurrent-poison-job",
                "background",
                "statistics",
                now
            ),
            now
        );

        await Task.WhenAll(
            Enumerable.Range(0, PerformanceOperationalRunWriterService.MaxOutboxRetryCount)
                .Select(async attempt =>
                {
                    using var db = CreateDb();
                    await PerformanceOperationalRunWriterService.RecordOutboxFailureAsync(
                        db,
                        outboxId,
                        now.AddMilliseconds(attempt),
                        new InvalidOperationException("test failure")
                    );
                })
        );

        var outbox = await _db
            .Queryable<PerformanceOperationalRunTransitionOutbox>()
            .SingleAsync();
        Assert.Equal(PerformanceOperationalRunWriterService.MaxOutboxRetryCount, outbox.RetryCount);
        Assert.NotNull(outbox.DeadLetteredAtUtc);
    }

    [Fact]
    public void Outbox到期判定_锁前锁后都拒绝未来重试和死信记录()
    {
        var now = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var outbox = new PerformanceOperationalRunTransitionOutbox
        {
            NextAttemptAtUtc = now.AddSeconds(1),
        };

        Assert.False(PerformanceOperationalRunWriterService.IsOutboxDue(null, now));
        Assert.False(PerformanceOperationalRunWriterService.IsOutboxDue(outbox, now));
        outbox.NextAttemptAtUtc = now;
        Assert.True(PerformanceOperationalRunWriterService.IsOutboxDue(outbox, now));
        outbox.DeadLetteredAtUtc = now;
        Assert.False(PerformanceOperationalRunWriterService.IsOutboxDue(outbox, now));
    }

    [Fact]
    public async Task Outbox应用失败_写退避前保持数据库锁并阻止并发实例通过重验()
    {
        var now = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var outboxId = await PerformanceOperationalRunWriterService.EnqueueAsync(
            _db,
            PerformanceOperationalRunTransition.Queued(
                "apply-failure-race-job",
                "background",
                "statistics",
                now
            ),
            now
        );
        await _db.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER fail_performance_run_insert
            BEFORE INSERT ON PerformanceOperationalRun
            BEGIN
                SELECT RAISE(ABORT, 'forced apply failure');
            END;
            """
        );
        using var firstDb = CreateDb();
        using var secondDb = CreateDb();
        var rolledBackToSavepoint = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var allowFailureRecord = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var first = PerformanceOperationalRunWriterService.ProcessOutboxAsync(
            firstDb,
            outboxId,
            Options.Create(new PerformanceMetricsOptions()),
            "instance-a",
            now,
            async () =>
            {
                rolledBackToSavepoint.TrySetResult();
                await allowFailureRecord.Task;
            }
        );
        await rolledBackToSavepoint.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var second = Task.Run(() =>
            PerformanceOperationalRunWriterService.ProcessOutboxAsync(
                secondDb,
                outboxId,
                Options.Create(new PerformanceMetricsOptions()),
                "instance-b",
                now
            )
        );
        await Task.Delay(150);
        var secondWasBlocked = !second.IsCompleted;
        allowFailureRecord.TrySetResult();

        await Assert.ThrowsAnyAsync<Exception>(async () => await first);
        Assert.True(secondWasBlocked);
        Assert.False(await second);
        var outbox = await _db
            .Queryable<PerformanceOperationalRunTransitionOutbox>()
            .SingleAsync();
        Assert.Equal(1, outbox.RetryCount);
        Assert.True(outbox.NextAttemptAtUtc > now);
        Assert.Equal(0, await _db.Queryable<PerformanceOperationalRun>().CountAsync());
    }

    [Fact]
    public async Task 生命周期转换_幂等持久化尝试次数时长并只记录一次完成指标()
    {
        var start = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var transitions = new[]
        {
            PerformanceOperationalRunTransition.Queued("job-1", "hq", "products", start, 4),
            PerformanceOperationalRunTransition.Started("job-1", "hq", "products", start.AddSeconds(1), 3),
            PerformanceOperationalRunTransition.Retry("job-1", "hq", "products", start.AddSeconds(3), 2),
            PerformanceOperationalRunTransition.Completed("job-1", "hq", "products", "success", start.AddSeconds(6)),
            PerformanceOperationalRunTransition.Completed("job-1", "hq", "products", "success", start.AddSeconds(6)),
        };

        foreach (var transition in transitions)
        {
            await PerformanceOperationalRunWriterService.ApplyAsync(
                _db,
                transition,
                Options.Create(new PerformanceMetricsOptions())
            );
        }

        var run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("success", run.Status);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(5000, run.DurationMs);
        Assert.Equal(2, run.Backlog);
    }

    [Fact]
    public async Task 生命周期转换_跳过是独立终态且不归一为失败()
    {
        var occurredAt = new DateTime(2026, 9, 14, 6, 30, 0, DateTimeKind.Utc);
        await PerformanceOperationalRunWriterService.ApplyAsync(
            _db,
            PerformanceOperationalRunTransition.Completed(
                "statistics-skipped",
                "statistics",
                "full-refresh",
                "skipped",
                occurredAt
            ),
            Options.Create(new PerformanceMetricsOptions())
        );

        var run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("skipped", run.Status);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task Outbox乱序处理_旧非终态不得覆盖较新重试且最终完成仍可落库()
    {
        var start = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        var queuedId = await PerformanceOperationalRunWriterService.EnqueueAsync(
            _db,
            PerformanceOperationalRunTransition.Queued(
                "out-of-order-job",
                "hq",
                "products",
                start,
                4
            ),
            start
        );
        var retryId = await PerformanceOperationalRunWriterService.EnqueueAsync(
            _db,
            PerformanceOperationalRunTransition.Retry(
                "out-of-order-job",
                "hq",
                "products",
                start.AddSeconds(2),
                2
            ),
            start
        );

        Assert.True(
            await PerformanceOperationalRunWriterService.ProcessOutboxAsync(
                _db,
                retryId,
                Options.Create(new PerformanceMetricsOptions()),
                "instance-a",
                start.AddSeconds(3)
            )
        );
        Assert.True(
            await PerformanceOperationalRunWriterService.ProcessOutboxAsync(
                _db,
                queuedId,
                Options.Create(new PerformanceMetricsOptions()),
                "instance-a",
                start.AddSeconds(3)
            )
        );

        var run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("retry_wait", run.Status);
        Assert.Equal(2, run.Attempt);
        Assert.Equal(2, run.Backlog);

        await PerformanceOperationalRunWriterService.ApplyAsync(
            _db,
            PerformanceOperationalRunTransition.Completed(
                "out-of-order-job",
                "hq",
                "products",
                "success",
                start.AddSeconds(4)
            ),
            Options.Create(new PerformanceMetricsOptions()),
            "instance-a"
        );
        run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("success", run.Status);
        Assert.Equal(2, run.Attempt);
    }

    [Fact]
    public async Task 启动恢复_把上个进程未完成运行标记为interrupted()
    {
        var queuedAt = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc);
        await _db.Insertable(
            new PerformanceOperationalRun
            {
                ExternalRunId = "stale-job",
                Category = "background",
                Operation = "statistics",
                Status = "running",
                Environment = "Production",
                Source = "backend",
                QueuedAtUtc = queuedAt,
                StartedAtUtc = queuedAt.AddMinutes(1),
            }
        ).ExecuteCommandAsync();
        var recoveredAt = queuedAt.AddMinutes(10);

        var count = await PerformanceOperationalRunWriterService.MarkInterruptedAsync(
            _db,
            recoveredAt
        );

        var run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal(1, count);
        Assert.Equal("interrupted", run.Status);
        Assert.Equal(recoveredAt, run.CompletedAtUtc);
        Assert.Equal(9 * 60 * 1000, run.DurationMs);
    }

    [Fact]
    public async Task 启动恢复_只中断过期租约且权威终态可覆盖interrupted()
    {
        var now = new DateTime(2026, 8, 25, 2, 0, 0, DateTimeKind.Utc);
        await _db.Insertable(new[]
        {
            new PerformanceOperationalRun
            {
                ExternalRunId = "active-on-other-instance",
                Category = "hq",
                Operation = "products",
                Status = "running",
                Attempt = 1,
                Environment = "Production",
                Source = "backend",
                OwnerInstanceId = "instance-a",
                LeaseExpiresAtUtc = now.AddMinutes(1),
                QueuedAtUtc = now.AddMinutes(-2),
                StartedAtUtc = now.AddMinutes(-1),
            },
            new PerformanceOperationalRun
            {
                ExternalRunId = "expired-on-dead-instance",
                Category = "hq",
                Operation = "prices",
                Status = "running",
                Attempt = 1,
                Environment = "Production",
                Source = "backend",
                OwnerInstanceId = "instance-b",
                LeaseExpiresAtUtc = now.AddSeconds(-1),
                QueuedAtUtc = now.AddMinutes(-3),
                StartedAtUtc = now.AddMinutes(-2),
            },
        }).ExecuteCommandAsync();

        var count = await PerformanceOperationalRunWriterService.MarkInterruptedAsync(
            _db,
            now
        );

        Assert.Equal(1, count);
        var active = await _db.Queryable<PerformanceOperationalRun>()
            .Where(item => item.ExternalRunId == "active-on-other-instance")
            .SingleAsync();
        Assert.Equal("running", active.Status);
        var interrupted = await _db.Queryable<PerformanceOperationalRun>()
            .Where(item => item.ExternalRunId == "expired-on-dead-instance")
            .SingleAsync();
        Assert.Equal("interrupted", interrupted.Status);

        await PerformanceOperationalRunWriterService.ApplyAsync(
            _db,
            PerformanceOperationalRunTransition.Completed(
                "expired-on-dead-instance",
                "hq",
                "prices",
                "success",
                now.AddSeconds(2)
            ),
            Options.Create(new PerformanceMetricsOptions()),
            "instance-b-process-2"
        );
        interrupted = await _db.Queryable<PerformanceOperationalRun>()
            .Where(item => item.ExternalRunId == "expired-on-dead-instance")
            .SingleAsync();
        Assert.Equal("success", interrupted.Status);
        Assert.Null(interrupted.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task 启动恢复_并发完成不得被陈旧运行快照覆盖()
    {
        var now = new DateTime(2026, 8, 25, 3, 0, 0, DateTimeKind.Utc);
        var runId = Guid.NewGuid();
        await _db.Insertable(
            new PerformanceOperationalRun
            {
                Id = runId,
                ExternalRunId = "recover-race-job",
                Category = "hq",
                Operation = "products",
                Status = "running",
                Attempt = 1,
                Environment = "Production",
                Source = "backend",
                OwnerInstanceId = "instance-a",
                LeaseExpiresAtUtc = now.AddSeconds(-1),
                LastTransitionAtUtc = now.AddMinutes(-2),
                QueuedAtUtc = now.AddMinutes(-3),
                StartedAtUtc = now.AddMinutes(-2),
            }
        ).ExecuteCommandAsync();
        var completedAt = now.AddMilliseconds(1);
        var injected = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (injected || !IsOperationalRunUpdate(sql))
            {
                return;
            }
            injected = true;
            using var concurrentDb = CreateDb();
            concurrentDb.Updateable<PerformanceOperationalRun>()
                .SetColumns(item => new PerformanceOperationalRun
                {
                    Status = "success",
                    CompletedAtUtc = completedAt,
                    DurationMs = 120_000,
                    LeaseExpiresAtUtc = null,
                    LastTransitionAtUtc = completedAt,
                })
                .Where(item => item.Id == runId)
                .ExecuteCommand();
        };

        try
        {
            var affected = await PerformanceOperationalRunWriterService.MarkInterruptedAsync(
                _db,
                now
            );
            Assert.Equal(0, affected);
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }

        Assert.True(injected);
        var run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("success", run.Status);
        Assert.Equal(completedAt, run.CompletedAtUtc);
        Assert.Null(run.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task 租约续期_并发完成不得被陈旧运行快照覆盖()
    {
        var now = new DateTime(2026, 8, 25, 4, 0, 0, DateTimeKind.Utc);
        var runId = Guid.NewGuid();
        await _db.Insertable(
            new PerformanceOperationalRun
            {
                Id = runId,
                ExternalRunId = "renew-race-job",
                Category = "background",
                Operation = "statistics",
                Status = "running",
                Attempt = 1,
                Environment = "Production",
                Source = "backend",
                OwnerInstanceId = "instance-a",
                LeaseExpiresAtUtc = now.AddSeconds(30),
                LastTransitionAtUtc = now.AddMinutes(-1),
                QueuedAtUtc = now.AddMinutes(-2),
                StartedAtUtc = now.AddMinutes(-1),
            }
        ).ExecuteCommandAsync();
        var completedAt = now.AddMilliseconds(1);
        var injected = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (injected || !IsOperationalRunUpdate(sql))
            {
                return;
            }
            injected = true;
            using var concurrentDb = CreateDb();
            concurrentDb.Updateable<PerformanceOperationalRun>()
                .SetColumns(item => new PerformanceOperationalRun
                {
                    Status = "success",
                    CompletedAtUtc = completedAt,
                    DurationMs = 60_000,
                    LeaseExpiresAtUtc = null,
                    LastTransitionAtUtc = completedAt,
                })
                .Where(item => item.Id == runId)
                .ExecuteCommand();
        };

        try
        {
            var affected = await PerformanceOperationalRunWriterService.RenewOwnedLeasesAsync(
                _db,
                "instance-a",
                now,
                new PerformanceMetricsOptions()
            );
            Assert.Equal(0, affected);
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }

        Assert.True(injected);
        var run = await _db.Queryable<PerformanceOperationalRun>().SingleAsync();
        Assert.Equal("success", run.Status);
        Assert.Equal(completedAt, run.CompletedAtUtc);
        Assert.Null(run.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task 周期租约清扫_先续当前实例再中断重启遗留的过期运行()
    {
        var now = new DateTime(2026, 8, 25, 5, 0, 0, DateTimeKind.Utc);
        await _db.Insertable(new[]
        {
            new PerformanceOperationalRun
            {
                ExternalRunId = "current-instance-job",
                Category = "background",
                Operation = "statistics",
                Status = "running",
                Attempt = 1,
                Environment = "Production",
                Source = "backend",
                OwnerInstanceId = "instance-new",
                LeaseExpiresAtUtc = now.AddSeconds(-1),
                QueuedAtUtc = now.AddMinutes(-2),
                StartedAtUtc = now.AddMinutes(-1),
            },
            new PerformanceOperationalRun
            {
                ExternalRunId = "previous-instance-job",
                Category = "hq",
                Operation = "products",
                Status = "running",
                Attempt = 1,
                Environment = "Production",
                Source = "backend",
                OwnerInstanceId = "instance-old",
                LeaseExpiresAtUtc = now.AddSeconds(-1),
                QueuedAtUtc = now.AddMinutes(-3),
                StartedAtUtc = now.AddMinutes(-2),
            },
        }).ExecuteCommandAsync();

        var interrupted = await PerformanceOperationalRunWriterService
            .RenewOwnedLeasesAndMarkInterruptedAsync(
                _db,
                "instance-new",
                now,
                new PerformanceMetricsOptions { OperationalRunLeaseSeconds = 120 }
            );

        Assert.Equal(1, interrupted);
        var current = await _db.Queryable<PerformanceOperationalRun>()
            .Where(item => item.ExternalRunId == "current-instance-job")
            .SingleAsync();
        Assert.Equal("running", current.Status);
        Assert.Equal(now.AddSeconds(120), current.LeaseExpiresAtUtc);
        var previous = await _db.Queryable<PerformanceOperationalRun>()
            .Where(item => item.ExternalRunId == "previous-instance-job")
            .SingleAsync();
        Assert.Equal("interrupted", previous.Status);
        Assert.Equal(now, previous.CompletedAtUtc);
    }

    [Fact]
    public async Task 数据库不可用时_内存回退队列有界且重试有限并指数退避()
    {
        var root = FindRepoRoot();
        var queueSource = await File.ReadAllTextAsync(
            Path.Combine(
                root,
                "services/backend/BlazorApp.Api/Services/Performance/PerformanceOperationalRunQueue.cs"
            )
        );
        var writerSource = await File.ReadAllTextAsync(
            Path.Combine(
                root,
                "services/backend/BlazorApp.Api/Services/Performance/PerformanceOperationalRunWriterService.cs"
            )
        );

        Assert.Contains("Channel.CreateBounded", queueSource, StringComparison.Ordinal);
        Assert.Contains("BoundedChannelFullMode.Wait", queueSource, StringComparison.Ordinal);
        Assert.Contains("InMemoryAttempt", queueSource, StringComparison.Ordinal);
        Assert.Contains("ReportDroppedFallback", queueSource, StringComparison.Ordinal);
        Assert.Contains("MaxInMemoryFallbackRetryCount", writerSource, StringComparison.Ordinal);
        Assert.Contains("InMemoryRetryDelay", writerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void 数据库不可用时_有界队列满后拒绝新回退并累计丢弃数()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var queue = new PerformanceOperationalRunQueue(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PerformanceOperationalRunQueue>.Instance,
            fallbackCapacity: 2
        );
        var now = new DateTime(2026, 8, 25, 6, 0, 0, DateTimeKind.Utc);

        Assert.True(
            queue.TryWrite(
                PerformanceOperationalRunTransition.Queued(
                    "fallback-1",
                    "background",
                    "statistics",
                    now
                )
            )
        );
        Assert.True(
            queue.TryWrite(
                PerformanceOperationalRunTransition.Queued(
                    "fallback-2",
                    "background",
                    "statistics",
                    now
                )
            )
        );
        Assert.False(
            queue.TryWrite(
                PerformanceOperationalRunTransition.Queued(
                    "fallback-3",
                    "background",
                    "statistics",
                    now
                )
            )
        );
        Assert.Equal(1, queue.DroppedFallbackCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(20, 30)]
    public void 内存回退重试_使用封顶三十秒的指数退避(int attempt, int seconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            PerformanceOperationalRunWriterService.InMemoryRetryDelay(attempt)
        );
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private SqlSugarClient CreateDb() =>
        new(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={_dbPath};Default Timeout=30",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

    private static bool IsOperationalRunUpdate(string sql) =>
        sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("PerformanceOperationalRun", StringComparison.OrdinalIgnoreCase)
        && !sql.Contains(
            "PerformanceOperationalRunTransitionOutbox",
            StringComparison.OrdinalIgnoreCase
        );

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var gitMarker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }

}
