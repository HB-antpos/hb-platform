using BlazorApp.Api.Services.Logging;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

public class ApplicationLogBackgroundServiceTests
{
    [Fact]
    public async Task FlushAsync_落库失败时累计失败统计并记录安全失败原因()
    {
        var queue = new ApplicationLogQueue(capacity: 10);
        queue.TryEnqueue(CreateItem("first"));
        queue.TryEnqueue(CreateItem("second"));

        using var cts = new CancellationTokenSource(AsyncTestWaitSupport.DefaultTimeout);
        var service = new TestableApplicationLogBackgroundService(
            queue,
            new ThrowingScopeFactory(new InvalidOperationException("模拟落库失败")),
            NullLogger<ApplicationLogBackgroundService>.Instance,
            Options.Create(
                new ApplicationLoggingOptions
                {
                    DefaultProjectCode = "HBBBackend",
                    MaxBatchSize = 10,
                }
            )
        );

        var runTask = service.RunAsync(cts.Token);
        await WaitForAsync(() => queue.GetRuntimeSnapshot().FailedFlushBatchCount > 0, cts.Token);
        cts.Cancel();
        await runTask;

        var snapshot = queue.GetRuntimeSnapshot();
        Assert.Equal(1, snapshot.FailedFlushBatchCount);
        Assert.Equal(2, snapshot.FailedFlushLogCount);
        Assert.Equal(2, snapshot.LastFailedFlushBatchSize);
        Assert.Equal(nameof(InvalidOperationException), snapshot.LastFailedFlushReason);
        Assert.DoesNotContain("模拟落库失败", snapshot.LastFailedFlushReason);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }

    private static ApplicationLogIngestItemDto CreateItem(string message)
    {
        return new ApplicationLogIngestItemDto
        {
            Level = "Error",
            Message = message,
            TimestampUtc = DateTime.UtcNow,
            ProjectCode = "HBBBackend",
            Environment = "test",
            SourceType = "Backend",
        };
    }

    private sealed class TestableApplicationLogBackgroundService : ApplicationLogBackgroundService
    {
        public TestableApplicationLogBackgroundService(
            IApplicationLogQueue queue,
            IServiceScopeFactory scopeFactory,
            Microsoft.Extensions.Logging.ILogger<ApplicationLogBackgroundService> logger,
            IOptions<ApplicationLoggingOptions> options
        )
            : base(queue, scopeFactory, logger, options) { }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            return ExecuteAsync(cancellationToken);
        }
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        private readonly Exception _exception;

        public ThrowingScopeFactory(Exception exception)
        {
            _exception = exception;
        }

        public IServiceScope CreateScope()
        {
            return new ThrowingScope(_exception);
        }
    }

    private sealed class ThrowingScope : IServiceScope
    {
        public ThrowingScope(Exception exception)
        {
            ServiceProvider = new ThrowingServiceProvider(exception);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() { }
    }

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        private readonly Exception _exception;

        public ThrowingServiceProvider(Exception exception)
        {
            _exception = exception;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ApplicationLogService))
                throw _exception;

            return null;
        }
    }
}
