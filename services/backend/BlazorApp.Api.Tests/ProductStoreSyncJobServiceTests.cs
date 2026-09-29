using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductStoreSyncJobServiceTests
{
    [Fact]
    public async Task StartJobAsync_提交后立即返回运行中任务并可查询完成结果()
    {
        var release = new TaskCompletionSource<ApiResponse<SyncProductsToStoresResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .Returns(release.Task);

        var service = CreateService(syncService);

        var started = await service.StartJobAsync(BuildRequest());

        Assert.Equal(ProductStoreSyncJobStatusConstants.Running, started.Status);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));

        release.SetResult(ApiResponse<SyncProductsToStoresResult>.OK(new SyncProductsToStoresResult { UpdatedCount = 2 }));
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(ProductStoreSyncJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.UpdatedCount);
    }

    [Fact]
    public async Task StartJobAsync_相同Operation运行中时复用同一个任务()
    {
        var release = new TaskCompletionSource<ApiResponse<SyncProductsToStoresResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .Returns(release.Task);

        var service = CreateService(syncService);

        var first = await service.StartJobAsync(BuildRequest());
        var duplicate = await service.StartJobAsync(BuildRequest());

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<SyncProductsToStoresResult>.OK(new SyncProductsToStoresResult { UpdatedCount = 1 }));
        await WaitForJobAsync(service, first.JobId);
        syncService.Verify(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()), Times.Once);
    }

    [Fact]
    public async Task StartJobAsync_已有两个不同Operation运行时重复Operation仍复用任务()
    {
        var release = new TaskCompletionSource<ApiResponse<SyncProductsToStoresResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .Returns(release.Task);

        var service = CreateService(syncService);

        var first = await service.StartJobAsync(BuildRequest(storeCodes: ["1033"]));
        await service.StartJobAsync(BuildRequest(storeCodes: ["1005"]));
        var duplicate = await service.StartJobAsync(BuildRequest(storeCodes: ["1033"]));

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<SyncProductsToStoresResult>.OK(new SyncProductsToStoresResult { UpdatedCount = 1 }));
        await WaitForJobAsync(service, first.JobId);
        syncService.Verify(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task StartJobAsync_已有两个不同Operation运行时第三个不同Operation被拒绝且不调用同步服务()
    {
        var release = new TaskCompletionSource<ApiResponse<SyncProductsToStoresResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .Returns(release.Task);

        var service = CreateService(syncService);

        await service.StartJobAsync(BuildRequest(storeCodes: ["1033"]));
        await service.StartJobAsync(BuildRequest(storeCodes: ["1005"]));
        await WaitForSyncInvocationCountAsync(syncService, 2);

        var error = await Assert.ThrowsAsync<ProductStoreSyncJobConcurrencyLimitExceededException>(() =>
            service.StartJobAsync(BuildRequest(storeCodes: ["1006"]))
        );

        Assert.Equal("PRODUCT_STORE_SYNC_JOB_LIMIT_EXCEEDED", error.ErrorCode);
        syncService.Verify(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()), Times.Exactly(2));

        release.SetResult(ApiResponse<SyncProductsToStoresResult>.OK(new SyncProductsToStoresResult { UpdatedCount = 1 }));
    }

    [Fact]
    public async Task StartJobAsync_完成后释放Operation允许再次提交()
    {
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .ReturnsAsync(ApiResponse<SyncProductsToStoresResult>.OK(new SyncProductsToStoresResult { UpdatedCount = 1 }));

        var service = CreateService(syncService);

        var first = await service.StartJobAsync(BuildRequest());
        await WaitForJobAsync(service, first.JobId);
        var second = await service.StartJobAsync(BuildRequest());
        await WaitForJobAsync(service, second.JobId);

        Assert.NotEqual(first.JobId, second.JobId);
        syncService.Verify(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task StartJobAsync_业务失败时保留失败结果和错误明细()
    {
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .ReturnsAsync(ApiResponse<SyncProductsToStoresResult>.Error(
                "同步商品到分店失败",
                "SYNC_ERROR",
                new SyncProductsToStoresResult { FailedCount = 1, Errors = ["1005 更新失败"] }
            ));

        var service = CreateService(syncService);

        var started = await service.StartJobAsync(BuildRequest());
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(ProductStoreSyncJobStatusConstants.Failed, completed.Status);
        Assert.Equal(1, completed.Result?.FailedCount);
        Assert.Contains("1005 更新失败", completed.Result?.Errors ?? []);
    }

    [Fact]
    public async Task StartJobAsync_执行异常时结果错误文案不透出原始异常并包含JobId()
    {
        const string rawError = "sql password leaked";
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .ThrowsAsync(new InvalidOperationException(rawError));

        var service = CreateService(syncService);

        var started = await service.StartJobAsync(BuildRequest());
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(ProductStoreSyncJobStatusConstants.Failed, completed.Status);
        Assert.DoesNotContain(rawError, completed.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.All(completed.Result?.Errors ?? [], error =>
        {
            Assert.DoesNotContain(rawError, error, StringComparison.Ordinal);
            Assert.Contains(started.JobId, error, StringComparison.Ordinal);
            Assert.Contains("商品同步任务执行失败", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task StartJobAsync_Fields只同步用户选择字段()
    {
        SyncProductsToStoresRequest? captured = null;
        var syncService = new Mock<IProductStoreSyncService>();
        syncService
            .Setup(service => service.SyncProductsToStoresAsync(It.IsAny<SyncProductsToStoresRequest>()))
            .Callback<SyncProductsToStoresRequest>(request => captured = request)
            .ReturnsAsync(ApiResponse<SyncProductsToStoresResult>.OK(new SyncProductsToStoresResult()));

        var service = CreateService(syncService);

        var started = await service.StartJobAsync(new SyncProductsToStoresRequest
        {
            ProductCodes = ["HB001"],
            StoreCodes = ["1005"],
            Fields = ["retailPrice"],
        });
        await WaitForJobAsync(service, started.JobId);

        Assert.False(captured?.SyncPurchasePrice);
        Assert.True(captured?.SyncRetailPrice);
        Assert.False(captured?.SyncIsAutoPricing);
        Assert.False(captured?.SyncIsSpecialProduct);
        Assert.False(captured?.SyncDiscountRate);
    }

    [Fact]
    public async Task ReactProductController_同步到分店Job接口返回Job并委托Job服务()
    {
        var authorize = typeof(ReactProductController)
            .GetMethod(nameof(ReactProductController.StartSyncProductsToStoresJob))!
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Permissions.PosProducts.Manage, authorize?.Policy);

        var jobService = new Mock<IProductStoreSyncJobService>(MockBehavior.Strict);
        jobService
            .Setup(service => service.StartJobAsync(
                It.Is<SyncProductsToStoresRequest>(request => request.ProductCodes.SequenceEqual(new[] { "HB001" })),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new SyncProductsToStoresJobDto
            {
                JobId = "job-1",
                OperationId = "product-store-sync|HB001|1005|retailPrice",
                Status = ProductStoreSyncJobStatusConstants.Running,
            });

        var controller = CreateController(jobService.Object);

        var response = await controller.StartSyncProductsToStoresJob(BuildRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        jobService.VerifyAll();
    }

    [Fact]
    public async Task ReactProductController_查询同步到分店Job返回结果并要求管理权限()
    {
        var authorize = typeof(ReactProductController)
            .GetMethod(nameof(ReactProductController.GetSyncProductsToStoresJob))!
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Permissions.PosProducts.Manage, authorize?.Policy);

        var jobService = new Mock<IProductStoreSyncJobService>(MockBehavior.Strict);
        jobService
            .Setup(service => service.GetJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncProductsToStoresJobDto
            {
                JobId = "job-1",
                Status = ProductStoreSyncJobStatusConstants.Succeeded,
                Result = new SyncProductsToStoresResult { UpdatedCount = 3 },
            });

        var controller = CreateController(jobService.Object);

        var response = await controller.GetSyncProductsToStoresJob("job-1", CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        jobService.VerifyAll();
    }

    private static ProductStoreSyncJobService CreateService(Mock<IProductStoreSyncService> syncService)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => syncService.Object);
        var provider = services.BuildServiceProvider();

        return new ProductStoreSyncJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductStoreSyncJobService>.Instance
        );
    }

    private static ReactProductController CreateController(IProductStoreSyncJobService jobService)
    {
        var scopeService = new Mock<ICurrentUserManageableStoreScopeService>();
        scopeService
            .Setup(service => service.GetScopeAsync())
            .ReturnsAsync(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAdmin = true,
            });

        return new ReactProductController(
            Mock.Of<IProductReactService>(),
            Mock.Of<IProductStoreSyncService>(),
            Mock.Of<IProductHqSyncService>(),
            scopeService.Object,
            Mock.Of<ILogger<ReactProductController>>(),
            Mock.Of<ICurrentUserService>(),
            supplierImageJobService: null,
            productStoreSyncJobService: jobService
        );
    }

    [Fact]
    public void BuildAggregateResponse_全部失败且没有创建更新时返回失败并保留结果()
    {
        var result = new SyncProductsToStoresResult
        {
            FailedCount = 2,
            Errors = ["分店 1005 同步失败", "分店 1033 同步失败"],
        };

        var response = ProductStoreSyncService.BuildAggregateResponse(result);

        Assert.False(response.Success);
        Assert.Same(result, response.Details);
        Assert.Same(result, response.Data);
        Assert.DoesNotContain("Exception", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunStoreSyncTasksAsync_分店同步最多三个并发()
    {
        var runningCount = 0;
        var maxRunningCount = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        var tasks = Enumerable.Range(0, 5)
            .Select(index => new Func<Task<int>>(async () =>
            {
                var running = Interlocked.Increment(ref runningCount);
                UpdateMax(ref maxRunningCount, running);
                if (Interlocked.Increment(ref startedCount) == 3)
                {
                    allStarted.SetResult();
                }

                await release.Task;
                Interlocked.Decrement(ref runningCount);
                return index;
            }))
            .ToList();

        var runTask = ProductStoreSyncService.RunStoreSyncTasksAsync(tasks);
        await allStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal(3, Volatile.Read(ref runningCount));
        Assert.Equal(3, Volatile.Read(ref maxRunningCount));

        release.SetResult();
        var results = await runTask;

        Assert.Equal(5, results.Count);
        Assert.Equal(3, maxRunningCount);
    }

    private static SyncProductsToStoresRequest BuildRequest(List<string>? storeCodes = null)
    {
        return new SyncProductsToStoresRequest
        {
            ProductCodes = ["HB001"],
            StoreCodes = storeCodes ?? ["1033", "1005"],
            Fields = ["retailPrice"],
        };
    }

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static async Task<SyncProductsToStoresJobDto> WaitForJobAsync(
        IProductStoreSyncJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current => current?.Status is ProductStoreSyncJobStatusConstants.Succeeded or ProductStoreSyncJobStatusConstants.Failed,
            describeLast: current => $"商品同步到分店 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private static Task WaitForSyncInvocationCountAsync(
        Mock<IProductStoreSyncService> syncService,
        int expectedCount
    ) =>
        WaitUntilAsync(
            () => syncService.Invocations.Count >= expectedCount,
            diagnostics: () => $"商品同步到分店服务已调用 {syncService.Invocations.Count} 次，期望至少 {expectedCount} 次"
        );
}
