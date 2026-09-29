using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class WarehouseProductBatchUpdateJobServiceTests
{
    [Fact]
    public async Task 后台任务_本地与HQ均成功时返回Succeeded()
    {
        var localService = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.Is<List<UpdateItemDto>>(items =>
                    items.Count == 1 && items[0].ProductCode == "P001"
                ),
                "操作员甲",
                It.Is<WarehouseProductBatchUpdateOptionsDto>(options =>
                    options.GenerateImageUrls
                    && options.SyncImageToHq
                    && options.ImageBaseUrl == "https://images.example.com/catalog/"
                )
            ))
            .ReturnsAsync(new WarehouseProductBatchUpdateResultDto
            {
                Success = true,
                Message = "更新完成",
                SuccessCount = 1,
                ImageUpdatedCount = 1,
                ImageUpdates =
                [
                    new ProductHqImageUpdateItemDto
                    {
                        ProductCode = "P001",
                        ImageUrl = "https://images.example.com/catalog/ITEM-1.jpg",
                    },
                ],
            });
        var hqService = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        hqService
            .Setup(service => service.SyncProductImagesAsync(
                It.Is<IReadOnlyCollection<ProductHqImageUpdateItemDto>>(items =>
                    items.Count == 1 && items.Single().ProductCode == "P001"
                ),
                "操作员甲",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new ProductHqImageSyncResultDto
            {
                Requested = true,
                Success = true,
                UpdatedCount = 1,
            });
        var service = CreateService(localService.Object, hqService.Object);

        var started = await service.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items = [new UpdateItemDto { ProductCode = "P001" }],
                GenerateImageUrls = true,
                ImageBaseUrl = "https://images.example.com/catalog///",
                SyncImageToHq = true,
            },
            "操作员甲"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(1, completed.Result?.ImageUpdatedCount);
        Assert.Equal(1, completed.Result?.HqImageSync.UpdatedCount);
        localService.VerifyAll();
        hqService.VerifyAll();
    }

    [Fact]
    public async Task 后台任务_本地或HQ存在逐项失败时返回PartiallySucceeded()
    {
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>(),
                It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
            ))
            .ReturnsAsync(new WarehouseProductBatchUpdateResultDto
            {
                Success = true,
                Message = "更新完成",
                SuccessCount = 1,
                FailedCount = 1,
                Errors = ["P002: 货号为空"],
                ImageUpdatedCount = 1,
                ImageUpdates =
                [
                    new ProductHqImageUpdateItemDto
                    {
                        ProductCode = "P001",
                        ImageUrl = "https://images.example.com/catalog/ITEM-1.jpg",
                    },
                ],
            });
        var hqService = new Mock<IProductHqSyncService>();
        hqService
            .Setup(service => service.SyncProductImagesAsync(
                It.IsAny<IReadOnlyCollection<ProductHqImageUpdateItemDto>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new ProductHqImageSyncResultDto
            {
                Requested = true,
                Success = false,
                FailedCount = 1,
                ErrorCode = "HQ_IMAGE_SYNC_ITEM_ERRORS",
                Errors = ["HQ 商品不存在: P001"],
            });
        var service = CreateService(localService.Object, hqService.Object);

        var started = await service.StartJobAsync(
            CreateImageJobRequest("P001", "P002"),
            "操作员甲"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(
            WarehouseProductBatchUpdateJobStatusConstants.PartiallySucceeded,
            completed.Status
        );
        Assert.Equal(1, completed.Result?.FailedCount);
        Assert.False(completed.Result?.HqImageSync.Success);
    }

    [Fact]
    public async Task 后台任务_本地事务失败时不调用HQ且状态为Failed()
    {
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>(),
                It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
            ))
            .ReturnsAsync(new WarehouseProductBatchUpdateResultDto
            {
                Success = false,
                Message = "批量更新失败",
            });
        var hqService = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        var service = CreateService(localService.Object, hqService.Object);

        var started = await service.StartJobAsync(CreateImageJobRequest("P001"), "操作员甲");
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Equal(1, completed.Result?.FailedCount);
        Assert.Equal(
            "HQ_IMAGE_SYNC_LOCAL_UPDATE_FAILED",
            completed.Result?.HqImageSync.ErrorCode
        );
        hqService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 后台任务_普通批量字段继续走既有重载并传递分店价格选项()
    {
        var localService = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.Is<List<UpdateItemDto>>(items =>
                    items.Count == 1
                    && items[0].ProductCode == "P001"
                    && items[0].ImportPrice == 1.25m
                    && items[0].SyncStorePurchasePrice == false
                ),
                "操作员甲"
            ))
            .ReturnsAsync(new BatchOperationResultDto
            {
                Success = true,
                Message = "更新完成",
                SuccessCount = 1,
            });
        var hqService = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        var service = CreateService(localService.Object, hqService.Object);

        var started = await service.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items =
                [
                    new UpdateItemDto
                    {
                        ProductCode = "P001",
                        ImportPrice = 1.25m,
                    },
                ],
                SyncStorePurchasePrice = false,
            },
            "操作员甲"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(1, completed.Result?.SuccessCount);
        localService.VerifyAll();
        hqService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 后台任务_国内供应商字段规范化后传递到本地服务()
    {
        var localService = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.Is<List<UpdateItemDto>>(items =>
                    items.Count == 1
                    && items[0].ProductCode == "P001"
                    && items[0].SupplierCode == "SUPPLIER-NEW"
                ),
                "操作员甲"
            ))
            .ReturnsAsync(new BatchOperationResultDto
            {
                Success = true,
                Message = "更新完成",
                SuccessCount = 1,
            });
        var service = CreateService(localService.Object, Mock.Of<IProductHqSyncService>());

        var started = await service.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items =
                [
                    new UpdateItemDto
                    {
                        ProductCode = " P001 ",
                        SupplierCode = " SUPPLIER-NEW ",
                    },
                ],
            },
            "操作员甲"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        localService.VerifyAll();
    }

    [Fact]
    public async Task 启动任务_国内供应商不同时不得错误复用正在执行的Job()
    {
        var release = new TaskCompletionSource<BatchOperationResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>()
            ))
            .Returns(release.Task);
        var service = CreateService(localService.Object, Mock.Of<IProductHqSyncService>());

        var first = await service.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items =
                [
                    new UpdateItemDto
                    {
                        ProductCode = "P001",
                        SupplierCode = "SUPPLIER-A",
                    },
                ],
            },
            "操作员甲"
        );
        var second = await service.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items =
                [
                    new UpdateItemDto
                    {
                        ProductCode = "P001",
                        SupplierCode = "SUPPLIER-B",
                    },
                ],
            },
            "操作员乙"
        );

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.False(second.IsDuplicateRequest);

        release.SetResult(new BatchOperationResultDto
        {
            Success = true,
            SuccessCount = 1,
        });
        await WaitForJobAsync(service, first.JobId);
        await WaitForJobAsync(service, second.JobId);
    }

    [Fact]
    public async Task 后台任务_不同请求串行执行避免重叠写入()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var invocation = 0;
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>(),
                It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
            ))
            .Returns(async () =>
            {
                var current = Interlocked.Increment(ref invocation);
                if (current == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    secondStarted.SetResult();
                }

                return new WarehouseProductBatchUpdateResultDto
                {
                    Success = true,
                    SuccessCount = 1,
                };
            });
        var service = CreateService(localService.Object, Mock.Of<IProductHqSyncService>());
        var firstRequest = CreateImageJobRequest("P001");
        firstRequest.SyncImageToHq = false;
        var secondRequest = CreateImageJobRequest("P002");
        secondRequest.SyncImageToHq = false;

        var first = await service.StartJobAsync(firstRequest, "操作员甲");
        await firstStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var second = await service.StartJobAsync(secondRequest, "操作员乙");

        var prematureSecondStart = await Task.WhenAny(
            secondStarted.Task,
            Task.Delay(TimeSpan.FromMilliseconds(100))
        );
        Assert.NotSame(secondStarted.Task, prematureSecondStart);

        releaseFirst.SetResult();
        await secondStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Equal(
            WarehouseProductBatchUpdateJobStatusConstants.Succeeded,
            (await WaitForJobAsync(service, first.JobId)).Status
        );
        Assert.Equal(
            WarehouseProductBatchUpdateJobStatusConstants.Succeeded,
            (await WaitForJobAsync(service, second.JobId)).Status
        );
    }

    [Fact]
    public async Task 启动任务_相同规范化请求运行中时复用同一Job()
    {
        var release = new TaskCompletionSource<WarehouseProductBatchUpdateResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>(),
                It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
            ))
            .Returns(release.Task);
        var service = CreateService(localService.Object, Mock.Of<IProductHqSyncService>());

        var first = await service.StartJobAsync(CreateImageJobRequest("P001", "P002"), "操作员甲");
        var duplicate = await service.StartJobAsync(
            CreateImageJobRequest(" P001 ", "P002"),
            "操作员乙"
        );

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(new WarehouseProductBatchUpdateResultDto
        {
            Success = true,
            SuccessCount = 2,
        });
        await WaitForJobAsync(service, first.JobId);
        localService.Verify(service => service.BatchUpdateAsync(
            It.IsAny<List<UpdateItemDto>>(),
            It.IsAny<string?>(),
            It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
        ), Times.Once);
    }

    [Fact]
    public async Task 启动任务_项目顺序不同时不得错误复用正在执行的Job()
    {
        var release = new TaskCompletionSource<WarehouseProductBatchUpdateResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .SetupSequence(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>(),
                It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
            ))
            .Returns(release.Task)
            .ReturnsAsync(new WarehouseProductBatchUpdateResultDto
            {
                Success = true,
                SuccessCount = 2,
            });
        var service = CreateService(localService.Object, Mock.Of<IProductHqSyncService>());
        var firstRequest = CreateImageJobRequest("P001", "P002");
        firstRequest.SyncImageToHq = false;
        var reorderedRequest = CreateImageJobRequest("P002", "P001");
        reorderedRequest.SyncImageToHq = false;

        var first = await service.StartJobAsync(firstRequest, "操作员甲");
        var reordered = await service.StartJobAsync(reorderedRequest, "操作员乙");

        Assert.NotEqual(first.JobId, reordered.JobId);
        Assert.False(reordered.IsDuplicateRequest);
        release.SetResult(new WarehouseProductBatchUpdateResultDto
        {
            Success = true,
            SuccessCount = 2,
        });
        await WaitForJobAsync(service, first.JobId);
        await WaitForJobAsync(service, reordered.JobId);
    }

    [Fact]
    public async Task 启动任务_队列达到上限时明确拒绝新任务()
    {
        var release = new TaskCompletionSource<WarehouseProductBatchUpdateResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var localService = new Mock<IProductWarehouseReactService>();
        localService
            .Setup(service => service.BatchUpdateAsync(
                It.IsAny<List<UpdateItemDto>>(),
                It.IsAny<string?>(),
                It.IsAny<WarehouseProductBatchUpdateOptionsDto>()
            ))
            .Returns(release.Task);
        var service = CreateService(
            localService.Object,
            Mock.Of<IProductHqSyncService>(),
            maxActiveJobs: 1
        );
        var firstRequest = CreateImageJobRequest("P001");
        firstRequest.SyncImageToHq = false;
        var secondRequest = CreateImageJobRequest("P002");
        secondRequest.SyncImageToHq = false;

        var first = await service.StartJobAsync(firstRequest, "操作员甲");
        await Assert.ThrowsAsync<WarehouseProductBatchUpdateQueueFullException>(() =>
            service.StartJobAsync(secondRequest, "操作员乙")
        );

        release.SetResult(new WarehouseProductBatchUpdateResultDto
        {
            Success = true,
            SuccessCount = 1,
        });
        await WaitForJobAsync(service, first.JobId);
    }

    private static WarehouseProductBatchUpdateJobRequestDto CreateImageJobRequest(
        params string[] productCodes
    )
    {
        return new WarehouseProductBatchUpdateJobRequestDto
        {
            Items = productCodes
                .Select(code => new UpdateItemDto { ProductCode = code })
                .ToList(),
            GenerateImageUrls = true,
            ImageBaseUrl = "https://images.example.com/catalog/",
            SyncImageToHq = true,
        };
    }

    private static WarehouseProductBatchUpdateJobService CreateService(
        IProductWarehouseReactService localService,
        IProductHqSyncService hqService,
        int maxActiveJobs = 20
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => localService);
        services.AddScoped(_ => hqService);
        var provider = services.BuildServiceProvider();

        return new WarehouseProductBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarehouseProductBatchUpdateJobService>.Instance,
            TimeProvider.System,
            TimeSpan.FromMinutes(45),
            maxActiveJobs
        );
    }

    private static async Task<WarehouseProductBatchUpdateJobDto> WaitForJobAsync(
        IWarehouseProductBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current =>
                current?.Status == WarehouseProductBatchUpdateJobStatusConstants.Succeeded
                || current?.Status == WarehouseProductBatchUpdateJobStatusConstants.PartiallySucceeded
                || current?.Status == WarehouseProductBatchUpdateJobStatusConstants.Failed,
            describeLast: current => $"仓库商品批量修改 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }
}
