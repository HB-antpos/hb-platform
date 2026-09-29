using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class WarehouseStorePriceSyncJobServiceTests
{
    [Fact]
    public async Task 启动任务_相同规范化操作运行中时复用同一Job()
    {
        var release = new TaskCompletionSource<ApiResponse<WarehouseStorePriceSyncResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncService = new Mock<IWarehouseStorePriceSyncService>();
        syncService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .Returns(release.Task);
        var service = CreateJobService(syncService.Object);

        var first = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = [" p02 ", "P01"],
                TargetStoreCodes = [" s02 ", "S01"],
                SyncToHq = true,
            },
            "admin"
        );
        var duplicate = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = ["p01", "P02", "P01"],
                TargetStoreCodes = ["s01", "S02"],
                SyncToHq = true,
            },
            "admin"
        );

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);
        Assert.Contains(
            duplicate.Status,
            new[]
            {
                WarehouseStorePriceSyncJobStatusConstants.Pending,
                WarehouseStorePriceSyncJobStatusConstants.Running,
            }
        );

        release.SetResult(ApiResponse<WarehouseStorePriceSyncResultDto>.OK(
            new WarehouseStorePriceSyncResultDto
            {
                RequestedProductCount = 1,
                EligibleProductCount = 1,
                LocalCommitted = true,
            }
        ));
        var completed = await WaitForJobAsync(service, first.JobId);
        Assert.Equal(WarehouseStorePriceSyncJobStatusConstants.Succeeded, completed.Status);
        syncService.Verify(service => service.ExecuteAsync(
            It.IsAny<WarehouseStorePriceSyncRequestDto>(),
            "admin",
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task 后台任务_不同但重叠的请求必须串行执行()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource<ApiResponse<WarehouseStorePriceSyncResultDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncService = new Mock<IWarehouseStorePriceSyncService>();
        syncService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .Returns<WarehouseStorePriceSyncRequestDto, string, CancellationToken>(
                async (request, _, _) =>
                {
                    if (request.ApplyToAllProducts)
                    {
                        firstStarted.TrySetResult();
                        return await releaseFirst.Task;
                    }

                    secondStarted.TrySetResult();
                    return ApiResponse<WarehouseStorePriceSyncResultDto>.OK(
                        new WarehouseStorePriceSyncResultDto
                        {
                            RequestedProductCount = 1,
                            EligibleProductCount = 1,
                            LocalCommitted = true,
                        }
                    );
                }
            );
        var service = CreateJobService(syncService.Object);

        var first = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ApplyToAllProducts = true,
                TargetStoreCodes = ["S01"],
            },
            "admin"
        );
        await firstStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var second = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = ["P01"],
                TargetStoreCodes = ["S01"],
            },
            "admin"
        );

        await Task.Delay(100);
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(
            WarehouseStorePriceSyncJobStatusConstants.Pending,
            (await service.GetJobAsync(second.JobId))?.Status
        );

        releaseFirst.SetResult(ApiResponse<WarehouseStorePriceSyncResultDto>.OK(
            new WarehouseStorePriceSyncResultDto
            {
                RequestedProductCount = 1,
                EligibleProductCount = 1,
                LocalCommitted = true,
            }
        ));
        Assert.Equal(
            WarehouseStorePriceSyncJobStatusConstants.Succeeded,
            (await WaitForJobAsync(service, first.JobId)).Status
        );
        await secondStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Equal(
            WarehouseStorePriceSyncJobStatusConstants.Succeeded,
            (await WaitForJobAsync(service, second.JobId)).Status
        );
    }

    [Fact]
    public async Task 后台任务_HQ失败但本地已提交时状态为PartiallySucceeded()
    {
        var result = new WarehouseStorePriceSyncResultDto
        {
            LocalCommitted = true,
            HqSucceeded = false,
            LocalCreatedCount = 1,
            Errors =
            [
                new WarehouseStorePriceSyncErrorDto
                {
                    Stage = "HqWrite",
                    Code = "HQ_FAILED",
                    Message = "HQ写入失败",
                },
            ],
        };
        var syncService = new Mock<IWarehouseStorePriceSyncService>();
        syncService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(ApiResponse<WarehouseStorePriceSyncResultDto>.Error(
                "HQ写入失败",
                "HQ_FAILED",
                result
            ));
        var service = CreateJobService(syncService.Object);

        var started = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = ["P01"],
                TargetStoreCodes = ["S01"],
                SyncToHq = true,
            },
            "admin"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseStorePriceSyncJobStatusConstants.PartiallySucceeded, completed.Status);
        Assert.True(completed.Result?.LocalCommitted);
        Assert.False(completed.Result?.HqSucceeded);
        Assert.Equal(1, completed.Result?.LocalCreatedCount);
    }

    [Fact]
    public async Task 后台任务_部分商品跳过但其余成功时状态为PartiallySucceeded()
    {
        var syncService = new Mock<IWarehouseStorePriceSyncService>();
        syncService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(ApiResponse<WarehouseStorePriceSyncResultDto>.OK(
                new WarehouseStorePriceSyncResultDto
                {
                    RequestedProductCount = 2,
                    EligibleProductCount = 1,
                    SkippedProductCount = 1,
                    LocalCommitted = true,
                    Errors =
                    [
                        new WarehouseStorePriceSyncErrorDto
                        {
                            Stage = "ProductSelection",
                            ProductCode = "P02",
                            Code = "MISSING_PRICE",
                            Message = "缺少价格",
                        },
                    ],
                }
            ));
        var service = CreateJobService(syncService.Object);

        var started = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = ["P01", "P02"],
                TargetStoreCodes = ["S01"],
            },
            "admin"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseStorePriceSyncJobStatusConstants.PartiallySucceeded, completed.Status);
        Assert.Equal(1, completed.Result?.SkippedProductCount);
        Assert.Single(completed.Result?.Errors ?? []);
    }

    [Fact]
    public async Task 后台任务_没有可处理商品时状态为Failed()
    {
        var syncService = new Mock<IWarehouseStorePriceSyncService>();
        syncService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(ApiResponse<WarehouseStorePriceSyncResultDto>.Error(
                "没有可同步的商品",
                "NO_ELIGIBLE_PRODUCTS",
                new WarehouseStorePriceSyncResultDto
                {
                    RequestedProductCount = 2,
                    EligibleProductCount = 0,
                    SkippedProductCount = 2,
                    LocalCommitted = false,
                    Errors =
                    [
                        new WarehouseStorePriceSyncErrorDto
                        {
                            Stage = "ProductSelection",
                            Code = "NO_ELIGIBLE_PRODUCTS",
                            Message = "没有可同步的商品",
                        },
                    ],
                }
            ));
        var service = CreateJobService(syncService.Object);

        var started = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = ["P01", "P02"],
                TargetStoreCodes = ["S01"],
            },
            "admin"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseStorePriceSyncJobStatusConstants.Failed, completed.Status);
        Assert.Equal(0, completed.Result?.EligibleProductCount);
        Assert.False(completed.Result?.LocalCommitted);
    }

    [Fact]
    public async Task 后台任务_未处理异常不向客户端泄露服务端详情()
    {
        const string sensitiveDetail = "Server=db.internal;Password=secret";
        var syncService = new Mock<IWarehouseStorePriceSyncService>();
        syncService
            .Setup(service => service.ExecuteAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new InvalidOperationException(sensitiveDetail));
        var service = CreateJobService(syncService.Object);

        var started = await service.StartJobAsync(
            new WarehouseStorePriceSyncRequestDto
            {
                ProductCodes = ["P01"],
                TargetStoreCodes = ["S01"],
            },
            "admin"
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseStorePriceSyncJobStatusConstants.Failed, completed.Status);
        var error = Assert.Single(completed.Result?.Errors ?? []);
        Assert.Equal("后台任务执行失败", error.Message);
        Assert.DoesNotContain(sensitiveDetail, error.Message);
    }

    [Fact]
    public async Task 控制器_审计用户名只从Claims传入后台任务()
    {
        var jobService = new Mock<IWarehouseStorePriceSyncJobService>();
        jobService
            .Setup(service => service.StartJobAsync(
                It.IsAny<WarehouseStorePriceSyncRequestDto>(),
                "claim-admin",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new WarehouseStorePriceSyncJobDto
            {
                JobId = "job-1",
                Status = WarehouseStorePriceSyncJobStatusConstants.Pending,
                CreatedAt = DateTime.UtcNow,
            });
        var controller = new ReactProductWarehouseStorePriceSyncController(
            Mock.Of<IWarehouseStorePriceSyncService>(),
            jobService.Object,
            NullLogger<ReactProductWarehouseStorePriceSyncController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "claim-admin")],
                        "Test"
                    )),
                },
            },
        };
        var request = new WarehouseStorePriceSyncRequestDto
        {
            ProductCodes = ["P01"],
            TargetStoreCodes = ["S01"],
        };

        var action = await controller.StartJob(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(action);
        Assert.Null(typeof(WarehouseStorePriceSyncRequestDto).GetProperty("UpdatedBy"));
        jobService.VerifyAll();
    }

    private static WarehouseStorePriceSyncJobService CreateJobService(
        IWarehouseStorePriceSyncService syncService
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => syncService);
        var provider = services.BuildServiceProvider();
        return new WarehouseStorePriceSyncJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarehouseStorePriceSyncJobService>.Instance
        );
    }

    private static async Task<WarehouseStorePriceSyncJobDto> WaitForJobAsync(
        IWarehouseStorePriceSyncJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current =>
                current?.Status == WarehouseStorePriceSyncJobStatusConstants.Succeeded
                || current?.Status == WarehouseStorePriceSyncJobStatusConstants.PartiallySucceeded
                || current?.Status == WarehouseStorePriceSyncJobStatusConstants.Failed,
            describeLast: current => $"仓库价格同步 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }
}
