using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LocalSupplierInvoiceBatchUpdateJobServiceTests
{
    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_提交后立即返回运行中任务()
    {
        var release = new TaskCompletionSource<ApiResponse<UpdateToStorePricesResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);

        var started = await service.StartUpdateToStorePricesJobAsync(
            BuildStoreRequest(),
            "tester"
        );

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running, started.Status);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Equal(["1005", "1033"], started.TargetStoreCodes);

        release.SetResult(ApiResponse<UpdateToStorePricesResultDto>.OK(new UpdateToStorePricesResultDto { Updated = 2 }));
        var completed = await WaitForStoreJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.Updated);
    }

    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_相同Operation运行中时复用任务()
    {
        var release = new TaskCompletionSource<ApiResponse<UpdateToStorePricesResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);

        var first = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var duplicate = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<UpdateToStorePricesResultDto>.OK(new UpdateToStorePricesResultDto { Updated = 1 }));
        await WaitForStoreJobAsync(service, first.JobId);
        storeService.Verify(service => service.UpdateDetailsToStorePricesAsync(It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_同张单不同Operation运行中时拒绝并发写入()
    {
        var release = new TaskCompletionSource<ApiResponse<UpdateToStorePricesResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);
        var first = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var conflictingRequest = BuildStoreRequest();
        conflictingRequest.UpdateFields = new UpdateToStorePricesFields { UpdateRetailPrice = true };

        var conflict = await Assert.ThrowsAsync<LocalSupplierInvoiceBatchUpdateJobConflictException>(
            () => service.StartUpdateToStorePricesJobAsync(conflictingRequest, "tester")
        );

        Assert.Equal(first.JobId, conflict.ExistingJobId);

        release.SetResult(ApiResponse<UpdateToStorePricesResultDto>.OK(new UpdateToStorePricesResultDto { Updated = 1 }));
        await WaitForStoreJobAsync(service, first.JobId);
        storeService.Verify(service => service.UpdateDetailsToStorePricesAsync(It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_业务失败可查询失败结果()
    {
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()))
            .ReturnsAsync(ApiResponse<UpdateToStorePricesResultDto>.Error(
                "更新到分店价格失败",
                "UPDATE_ERROR",
                new UpdateToStorePricesResultDto { Failed = 1, Errors = ["测试失败"] }
            ));

        var service = CreateService(storeService: storeService);

        var started = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var completed = await WaitForStoreJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal(1, completed.Result?.Failed);
        Assert.Contains("更新到分店价格失败", completed.Message);
    }

    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_成本锁忙释放后重试并使用全新请求和scope()
    {
        var responses = new Queue<ApiResponse<UpdateToStorePricesResultDto>>([
            ApiResponse<UpdateToStorePricesResultDto>.Error(
                "分店成本锁繁忙",
                "STORE_UPDATE_COST_LOCK_BUSY",
                new UpdateToStorePricesResultDto()
            ),
            ApiResponse<UpdateToStorePricesResultDto>.OK(new UpdateToStorePricesResultDto { Updated = 2 })
        ]);
        var capturedRequests = new List<UpdateToStorePricesRequest>();
        var lockWaitMilliseconds = new List<int>();
        TrackingScopeFactory? scopeFactory = null;
        var firstScopeReleased = false;
        var storeService = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(
                It.IsAny<UpdateToStorePricesRequest>(),
                "tester",
                It.IsAny<int>()
            ))
            .Callback<UpdateToStorePricesRequest, string, int>((request, _, lockWait) =>
            {
                capturedRequests.Add(request);
                lockWaitMilliseconds.Add(lockWait);
                if (capturedRequests.Count == 2)
                    firstScopeReleased = scopeFactory!.Scopes[0].Disposed;
                if (capturedRequests.Count == 1)
                    request.DetailGuids[0] = "mutated-only-on-first-attempt";
            })
            .ReturnsAsync(() => responses.Dequeue());

        var service = CreateService(
            storeService: storeService,
            scopeFactoryFactory: provider => scopeFactory = new TrackingScopeFactory(
                provider.GetRequiredService<IServiceScopeFactory>()
            )
        );
        var started = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var completed = await WaitForStoreJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.Updated);
        Assert.Equal(2, capturedRequests.Count);
        Assert.NotSame(capturedRequests[0], capturedRequests[1]);
        Assert.Equal(["detail-1", "detail-2"], capturedRequests[1].DetailGuids);
        Assert.Equal(2, lockWaitMilliseconds.Count);
        Assert.All(lockWaitMilliseconds, value => Assert.InRange(value, 1, 10_000));
        Assert.True(firstScopeReleased);
        Assert.Equal(2, scopeFactory!.Scopes.Count);
    }

    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_成本锁预算到期后不再发起新尝试()
    {
        var timeProvider = new SequenceTimeProvider(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(61)
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(
                It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()
            ))
            .ReturnsAsync(ApiResponse<UpdateToStorePricesResultDto>.Error(
                "分店成本锁繁忙",
                "STORE_UPDATE_COST_LOCK_BUSY",
                new UpdateToStorePricesResultDto()
            ));

        var service = CreateService(storeService: storeService, timeProvider: timeProvider);
        var started = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var completed = await WaitForStoreJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Contains("分店价格更新繁忙", completed.Message);
        storeService.Verify(service => service.UpdateDetailsToStorePricesAsync(
            It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateToStorePricesJobAsync_最后一次尝试只使用剩余锁预算()
    {
        var timeProvider = new SequenceTimeProvider(
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(60)
        );
        var waits = new List<int>();
        var storeService = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        storeService.Setup(service => service.UpdateDetailsToStorePricesAsync(
                It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()))
            .Callback<UpdateToStorePricesRequest, string, int>((_, _, wait) => waits.Add(wait))
            .ReturnsAsync(ApiResponse<UpdateToStorePricesResultDto>.Error(
                "分店成本锁繁忙", "STORE_UPDATE_COST_LOCK_BUSY", new UpdateToStorePricesResultDto()));

        var service = CreateService(storeService: storeService, timeProvider: timeProvider);
        var started = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var completed = await WaitForStoreJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal(2, waits.Count);
        Assert.Equal(10_000, waits[0]);
        Assert.InRange(waits[1], 1, 1_000);
        Assert.Equal(0, completed.Result?.Updated);
    }

    [Theory]
    [InlineData("VALIDATION_ERROR")]
    [InlineData("UPDATE_ERROR")]
    public async Task StartUpdateToStorePricesJobAsync_普通错误或提交结果未知不重试(string errorCode)
    {
        var storeService = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        storeService
            .Setup(service => service.UpdateDetailsToStorePricesAsync(
                It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()
            ))
            .ReturnsAsync(ApiResponse<UpdateToStorePricesResultDto>.Error(
                "分店更新失败",
                errorCode,
                new UpdateToStorePricesResultDto { Failed = errorCode == "UPDATE_ERROR" ? 1 : 0 }
            ));

        var service = CreateService(storeService: storeService);
        var started = await service.StartUpdateToStorePricesJobAsync(BuildStoreRequest(), "tester");
        var completed = await WaitForStoreJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        storeService.Verify(service => service.UpdateDetailsToStorePricesAsync(
            It.IsAny<UpdateToStorePricesRequest>(), "tester", It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_成功后可查询结果()
    {
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>();
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .ReturnsAsync(ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Updated = 3 }));

        var service = CreateService(hqService: hqService);

        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(3, completed.Result?.Updated);
        Assert.Equal(["1005", "1033"], completed.TargetStoreCodes);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_业务处理超过锁预算仍成功且不重放()
    {
        long timestamp = 0;
        var clock = new Mock<TimeProvider> { CallBase = true };
        clock.SetupGet(provider => provider.TimestampFrequency).Returns(1_000);
        clock.Setup(provider => provider.GetTimestamp()).Returns(() => Interlocked.Read(ref timestamp));
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService.Setup(service => service.UpdateHqProductsAsync(
                "invoice-1", It.IsAny<UpdateHqProductsRequest>(), null, "tester", 10_000))
            .ReturnsAsync(() =>
            {
                // 成功获得本地锁后，正常业务即使耗时超过 60 秒也不能被竞争预算取消或重放。
                Interlocked.Exchange(ref timestamp, 90_000);
                return ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Total = 2, Updated = 2 });
            });
        var service = CreateService(hqService: hqService, timeProvider: clock.Object);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);
        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.Updated);
        Assert.Equal(TimeSpan.FromSeconds(90), clock.Object.GetElapsedTime(0));
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1", It.IsAny<UpdateHqProductsRequest>(), null, "tester", 10_000), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_成本锁冲突后重试并使用全新请求()
    {
        var responses = new Queue<ApiResponse<UpdateHqProductsResult>>([
            ApiResponse<UpdateHqProductsResult>.Error(
                "成本锁繁忙",
                "HQ_UPDATE_COST_LOCK_BUSY",
                new UpdateHqProductsResult { Total = 2 }),
            ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Updated = 2 }),
        ]);
        var capturedRequests = new List<UpdateHqProductsRequest>();
        var lockWaitMilliseconds = new List<int>();
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .Callback<string, UpdateHqProductsRequest, string?, string, int>((_, request, _, _, lockWait) =>
            {
                capturedRequests.Add(request);
                lockWaitMilliseconds.Add(lockWait);
                if (capturedRequests.Count == 1)
                    request.DetailGuids[0] = "mutated-only-on-first-attempt";
            })
            .ReturnsAsync(() => responses.Dequeue());

        var service = CreateService(hqService: hqService);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.Updated);
        Assert.Equal(2, capturedRequests.Count);
        Assert.NotSame(capturedRequests[0], capturedRequests[1]);
        Assert.Equal(["detail-1", "detail-2"], capturedRequests[1].DetailGuids);
        Assert.Equal(2, lockWaitMilliseconds.Count);
        Assert.All(lockWaitMilliseconds, value => Assert.InRange(value, 1, 10_000));
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_重试等待前释放上一次scope()
    {
        TrackingScopeFactory? scopeFactory = null;
        var scopeWasReleasedBeforeSecondAttempt = false;
        var attempt = 0;
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .Callback<string, UpdateHqProductsRequest, string?, string, int>((_, _, _, _, _) =>
            {
                attempt++;
                if (attempt == 2)
                    scopeWasReleasedBeforeSecondAttempt = scopeFactory!.Scopes[0].Disposed;
            })
            .ReturnsAsync(() => attempt == 1
                ? ApiResponse<UpdateHqProductsResult>.Error(
                    "成本锁繁忙",
                    "HQ_UPDATE_COST_LOCK_BUSY",
                    new UpdateHqProductsResult { Total = 1 })
                : ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Updated = 1 }));

        var service = CreateService(
            hqService: hqService,
            scopeFactoryFactory: provider => scopeFactory = new TrackingScopeFactory(
                provider.GetRequiredService<IServiceScopeFactory>()));
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.True(scopeWasReleasedBeforeSecondAttempt);
        Assert.Equal(2, scopeFactory!.Scopes.Count);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_成本锁预算耗尽后不再发起新尝试()
    {
        var timeProvider = new SequenceTimeProvider(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(61)
        );
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .ReturnsAsync(ApiResponse<UpdateHqProductsResult>.Error(
                "成本锁繁忙",
                "HQ_UPDATE_COST_LOCK_BUSY",
                new UpdateHqProductsResult { Total = 6 }));

        var service = CreateService(hqService: hqService, timeProvider: timeProvider);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal("商品更新繁忙，等待其他成本操作超时，本次未更新 HQ 商品", completed.Message);
        Assert.Equal(6, completed.Result?.Total);
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1",
            It.IsAny<UpdateHqProductsRequest>(),
            null,
            "tester",
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_最后一次尝试只使用剩余预算并保留busy结果()
    {
        var timeProvider = new SequenceTimeProvider(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(59.5),
            TimeSpan.FromSeconds(59.5),
            TimeSpan.FromSeconds(60)
        );
        var lockWaitMilliseconds = new List<int>();
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .Callback<string, UpdateHqProductsRequest, string?, string, int>((_, _, _, _, lockWait) =>
                lockWaitMilliseconds.Add(lockWait))
            .ReturnsAsync(ApiResponse<UpdateHqProductsResult>.Error(
                "成本锁繁忙",
                "HQ_UPDATE_COST_LOCK_BUSY",
                new UpdateHqProductsResult { Total = 6 }));

        var service = CreateService(hqService: hqService, timeProvider: timeProvider);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal(6, completed.Result?.Total);
        Assert.Equal(2, lockWaitMilliseconds.Count);
        Assert.Equal(10_000, lockWaitMilliseconds[0]);
        Assert.InRange(lockWaitMilliseconds[1], 1, 1_000);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_普通错误不重试()
    {
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .ReturnsAsync(ApiResponse<UpdateHqProductsResult>.Error("参数错误", "VALIDATION_ERROR", new UpdateHqProductsResult()));

        var service = CreateService(hqService: hqService);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal("参数错误", completed.Message);
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1",
            It.IsAny<UpdateHqProductsRequest>(),
            null,
            "tester",
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_部分HQ成功结果不重试()
    {
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .ReturnsAsync(ApiResponse<UpdateHqProductsResult>.FailWithData(
                new UpdateHqProductsResult { Updated = 1 },
                "HQ 部分更新失败",
                "HQ_UPDATE_COST_LOCK_BUSY"));

        var service = CreateService(hqService: hqService);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal(1, completed.Result?.Updated);
        Assert.Equal("HQ 部分更新失败", completed.Message);
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1",
            It.IsAny<UpdateHqProductsRequest>(),
            null,
            "tester",
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_取消异常不重试()
    {
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .ThrowsAsync(new OperationCanceledException("已取消"));

        var service = CreateService(hqService: hqService);
        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Contains("已取消", completed.Message);
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1",
            It.IsAny<UpdateHqProductsRequest>(),
            null,
            "tester",
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_指定审计操作者后向后台同步传递GUID和姓名()
    {
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                "actor-guid-1",
                "审计操作员",
                It.IsAny<int>()
            ))
            .ReturnsAsync(ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Updated = 1 }));

        var service = CreateService(hqService: hqService);

        var started = await service.StartUpdateHqProductsJobAsync(
            "invoice-1",
            BuildHqRequest(),
            "actor-guid-1",
            "审计操作员"
        );
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        hqService.VerifyAll();
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_相同Operation运行中时复用任务()
    {
        var release = new TaskCompletionSource<ApiResponse<UpdateHqProductsResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>();
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .Returns(release.Task);

        var service = CreateService(hqService: hqService);

        var first = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var duplicate = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Updated = 1 }));
        await WaitForHqJobAsync(service, first.JobId);
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1",
            It.IsAny<UpdateHqProductsRequest>(),
            null,
            "tester",
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_同张单不同Operation运行中时拒绝并发写入()
    {
        var release = new TaskCompletionSource<ApiResponse<UpdateHqProductsResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>();
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .Returns(release.Task);

        var service = CreateService(hqService: hqService);
        var first = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var conflictingRequest = BuildHqRequest();
        conflictingRequest.IdempotencyKey = "idem-2";

        var conflict = await Assert.ThrowsAsync<LocalSupplierInvoiceBatchUpdateJobConflictException>(
            () => service.StartUpdateHqProductsJobAsync("invoice-1", conflictingRequest, "tester")
        );

        Assert.Equal(first.JobId, conflict.ExistingJobId);

        release.SetResult(ApiResponse<UpdateHqProductsResult>.OK(new UpdateHqProductsResult { Updated = 1 }));
        await WaitForHqJobAsync(service, first.JobId);
        hqService.Verify(service => service.UpdateHqProductsAsync(
            "invoice-1",
            It.IsAny<UpdateHqProductsRequest>(),
            null,
            "tester",
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task StartUpdateHqProductsJobAsync_异常失败可查询失败结果()
    {
        var hqService = new Mock<ILocalSupplierInvoiceHqProductSyncService>();
        hqService
            .Setup(service => service.UpdateHqProductsAsync(
                "invoice-1",
                It.IsAny<UpdateHqProductsRequest>(),
                null,
                "tester",
                It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("HQ 连接超时"));

        var service = CreateService(hqService: hqService);

        var started = await service.StartUpdateHqProductsJobAsync("invoice-1", BuildHqRequest(), "tester");
        var completed = await WaitForHqJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal(1, completed.Result?.Failed);
        Assert.Contains("HQ 连接超时", completed.Message);
    }

    [Fact]
    public async Task StartPasteDetailsJobAsync_提交后立即返回运行中任务并可查询完成结果()
    {
        var release = new TaskCompletionSource<ApiResponse<BatchResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.PasteDetailsAsync(It.IsAny<PasteDetailsRequest>(), "tester"))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);

        var started = await service.StartPasteDetailsJobAsync(BuildPasteRequest(), "tester");

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running, started.Status);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Equal("invoice-1", started.InvoiceGuid);

        release.SetResult(ApiResponse<BatchResultDto>.OK(new BatchResultDto { Inserted = 2, Updated = 1 }));
        var completed = await WaitForPasteJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.Inserted);
        Assert.Equal(1, completed.Result?.Updated);
    }

    [Fact]
    public async Task StartPasteDetailsJobAsync_后台任务保留多条码副码()
    {
        var capturedRequest = new TaskCompletionSource<PasteDetailsRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<ApiResponse<BatchResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.PasteDetailsAsync(It.IsAny<PasteDetailsRequest>(), "tester"))
            .Callback<PasteDetailsRequest, string>((request, _) => capturedRequest.SetResult(request))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);

        var started = await service.StartPasteDetailsJobAsync(BuildPasteRequest(
            barcode: "191554882676",
            additionalBarcodes:
            [
                "191554882690",
                "191554882669",
                "191554888425",
                "191554882706",
                "191554882652",
                "191554882683",
            ]
        ), "tester");
        var captured = await capturedRequest.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var item = Assert.Single(captured.Items);
        Assert.Equal("191554882676", item.Barcode);
        Assert.Equal(
            [
                "191554882690",
                "191554882669",
                "191554888425",
                "191554882706",
                "191554882652",
                "191554882683",
            ],
            item.AdditionalBarcodes
        );

        release.SetResult(ApiResponse<BatchResultDto>.OK(new BatchResultDto { Inserted = 1 }));
        await WaitForPasteJobAsync(service, started.JobId);
    }

    [Fact]
    public async Task StartPasteDetailsJobAsync_同主条码不同副码运行中时视为不同任务()
    {
        var release = new TaskCompletionSource<ApiResponse<BatchResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.PasteDetailsAsync(It.IsAny<PasteDetailsRequest>(), "tester"))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);
        var first = await service.StartPasteDetailsJobAsync(BuildPasteRequest(
            barcode: "191554882676",
            additionalBarcodes: ["191554882690"]
        ), "tester");
        var conflictingRequest = BuildPasteRequest(
            barcode: "191554882676",
            additionalBarcodes: ["191554882669"]
        );

        var conflict = await Assert.ThrowsAsync<LocalSupplierInvoiceBatchUpdateJobConflictException>(
            () => service.StartPasteDetailsJobAsync(conflictingRequest, "tester")
        );

        Assert.Equal(first.JobId, conflict.ExistingJobId);

        release.SetResult(ApiResponse<BatchResultDto>.OK(new BatchResultDto { Inserted = 1 }));
        await WaitForPasteJobAsync(service, first.JobId);
        storeService.Verify(service => service.PasteDetailsAsync(It.IsAny<PasteDetailsRequest>(), "tester"), Times.Once);
    }

    [Fact]
    public async Task StartCheckProductsJobAsync_提交后立即返回运行中任务并可查询完成结果()
    {
        var release = new TaskCompletionSource<ApiResponse<CheckProductsResponseDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.IsAny<CheckProductsRequest>()))
            .Returns(release.Task);

        var service = CreateService(storeService: storeService);

        var started = await service.StartCheckProductsJobAsync(BuildCheckProductsRequest());

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running, started.Status);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Equal("invoice-1", started.InvoiceGuid);

        release.SetResult(ApiResponse<CheckProductsResponseDto>.OK(new CheckProductsResponseDto
        {
            Summary = new CheckProductsSummaryDto { Total = 2, ProductExists = 1, ProductNotExists = 1 },
        }));
        var completed = await WaitForCheckProductsJobAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(2, completed.Result?.Summary.Total);
        Assert.Equal(1, completed.Result?.Summary.ProductExists);
    }

    private static LocalSupplierInvoiceBatchUpdateJobService CreateService(
        Mock<ILocalSupplierInvoicesReactService>? storeService = null,
        Mock<ILocalSupplierInvoiceHqProductSyncService>? hqService = null,
        TimeProvider? timeProvider = null,
        Func<IServiceProvider, IServiceScopeFactory>? scopeFactoryFactory = null
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => storeService?.Object ?? Mock.Of<ILocalSupplierInvoicesReactService>());
        services.AddScoped(_ => hqService?.Object ?? Mock.Of<ILocalSupplierInvoiceHqProductSyncService>());
        var provider = services.BuildServiceProvider();

        var scopeFactory = scopeFactoryFactory?.Invoke(provider)
            ?? provider.GetRequiredService<IServiceScopeFactory>();
        return new LocalSupplierInvoiceBatchUpdateJobService(
            scopeFactory,
            NullLogger<LocalSupplierInvoiceBatchUpdateJobService>.Instance,
            timeProvider
        );
    }

    private sealed class SequenceTimeProvider(params TimeSpan[] elapsedValues) : TimeProvider
    {
        private readonly Queue<long> _timestamps = new(
            new[] { 0L }.Concat(elapsedValues.Select(value => (long)value.TotalMilliseconds))
        );
        private long _lastTimestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp()
        {
            if (_timestamps.TryDequeue(out var timestamp))
                _lastTimestamp = timestamp;
            return _lastTimestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            // 测试中的重试等待立即推进；下一次 GetElapsedTime 从队列读取推进后的预算。
            callback(state);
            return new ImmediateTimer();
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public List<TrackingScope> Scopes { get; } = new();

        public IServiceScope CreateScope()
        {
            var scope = new TrackingScope(inner.CreateScope());
            Scopes.Add(scope);
            return scope;
        }
    }

    private sealed class TrackingScope(IServiceScope inner) : IServiceScope
    {
        public bool Disposed { get; private set; }

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    private static UpdateToStorePricesRequest BuildStoreRequest()
    {
        return new UpdateToStorePricesRequest
        {
            InvoiceGuid = "invoice-1",
            DetailGuids = ["detail-1", "detail-2"],
            TargetStoreCodes = ["1033", "1005"],
            UpdateFields = new UpdateToStorePricesFields { UpdatePurchasePrice = true },
        };
    }

    private static UpdateHqProductsRequest BuildHqRequest()
    {
        return new UpdateHqProductsRequest
        {
            DetailGuids = ["detail-1", "detail-2"],
            TargetStoreCodes = ["1033", "1005"],
            UpdateFields = new UpdateToStorePricesFields { UpdatePurchasePrice = true },
            IdempotencyKey = "idem-1",
        };
    }

    private static PasteDetailsRequest BuildPasteRequest(
        string barcode = "BAR-1",
        List<string>? additionalBarcodes = null
    )
    {
        return new PasteDetailsRequest
        {
            InvoiceGuid = "invoice-1",
            Mode = "append",
            Items =
            [
                new PastedDetailItemDto
                {
                    ItemNumber = "ITEM-1",
                    Barcode = barcode,
                    AdditionalBarcodes = additionalBarcodes ?? new List<string>(),
                    ProductName = "Paste Item",
                    Quantity = 2,
                    PurchasePrice = 1.5m,
                },
            ],
        };
    }

    private static CheckProductsRequest BuildCheckProductsRequest()
    {
        return new CheckProductsRequest
        {
            InvoiceGuid = "invoice-1",
            DetailGuids = ["detail-1", "detail-2"],
        };
    }

    private static async Task<LocalSupplierInvoiceUpdateToStorePricesJobDto> WaitForStoreJobAsync(
        ILocalSupplierInvoiceBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetUpdateToStorePricesJobAsync(jobId),
            current => current?.Status is LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded or LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
            describeLast: current => $"更新到分店价格 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private static async Task<LocalSupplierInvoiceUpdateHqProductsJobDto> WaitForHqJobAsync(
        ILocalSupplierInvoiceBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetUpdateHqProductsJobAsync(jobId),
            current => current?.Status is LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded or LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
            describeLast: current => $"更新HQ商品 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private static async Task<LocalSupplierInvoicePasteDetailsJobDto> WaitForPasteJobAsync(
        ILocalSupplierInvoiceBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetPasteDetailsJobAsync(jobId),
            current => current?.Status is LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded or LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
            describeLast: current => $"粘贴明细 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private static async Task<LocalSupplierInvoiceCheckProductsJobDto> WaitForCheckProductsJobAsync(
        ILocalSupplierInvoiceBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetCheckProductsJobAsync(jobId),
            current => current?.Status is LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded or LocalSupplierInvoiceBatchUpdateJobStatusConstants.Failed,
            describeLast: current => $"商品检测 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }
}
