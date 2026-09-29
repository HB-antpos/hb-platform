using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class WarehouseProductHqSyncJobServiceTests
{
    [Fact]
    public async Task StartJobAsync_后台成功后状态为Succeeded并携带同步结果()
    {
        var expected = new SyncResult
        {
            IsSuccess = true,
            Message = "WarehouseProduct 同步成功，新增 2 条，更新 3 条",
            AddedCount = 2,
            UpdatedCount = 3,
        };
        var syncServiceMock = new Mock<IProductWarehouseReactService>();
        syncServiceMock.Setup(service => service.SyncFromHqAsync()).ReturnsAsync(expected);

        var service = CreateService(syncServiceMock);

        var started = await service.StartJobAsync(
            new WarehouseProductHqSyncJobRequestDto { OperationId = "sync-success" }
        );

        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductHqSyncJobStatusConstants.Succeeded, completed.Status);
        Assert.NotNull(completed.Result);
        Assert.Equal(2, completed.Result!.AddedCount);
        Assert.Equal(3, completed.Result.UpdatedCount);
        Assert.Equal("WarehouseProduct 同步成功，新增 2 条，更新 3 条", completed.Message);
    }

    [Fact]
    public async Task StartJobAsync_已有运行中任务时返回同一个Job()
    {
        var releaseSync = new TaskCompletionSource<SyncResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var syncServiceMock = new Mock<IProductWarehouseReactService>();
        syncServiceMock.Setup(service => service.SyncFromHqAsync()).Returns(releaseSync.Task);

        var service = CreateService(syncServiceMock);

        var first = await service.StartJobAsync(
            new WarehouseProductHqSyncJobRequestDto { OperationId = "sync-first" }
        );
        var duplicate = await service.StartJobAsync(
            new WarehouseProductHqSyncJobRequestDto { OperationId = "sync-second" }
        );

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        releaseSync.SetResult(
            new SyncResult
            {
                IsSuccess = true,
                Message = "完成",
            }
        );
        await WaitForJobAsync(service, first.JobId);
        syncServiceMock.Verify(service => service.SyncFromHqAsync(), Times.Once);
    }

    [Fact]
    public async Task StartJobAsync_同步服务抛异常时状态为Failed()
    {
        var syncServiceMock = new Mock<IProductWarehouseReactService>();
        syncServiceMock
            .Setup(service => service.SyncFromHqAsync())
            .ThrowsAsync(new InvalidOperationException("HQ 连接失败"));

        var service = CreateService(syncServiceMock);

        var started = await service.StartJobAsync(
            new WarehouseProductHqSyncJobRequestDto { OperationId = "sync-failed" }
        );
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductHqSyncJobStatusConstants.Failed, completed.Status);
        Assert.NotNull(completed.Result);
        Assert.False(completed.Result!.IsSuccess);
        Assert.Contains("HQ 连接失败", completed.Message);
    }

    [Fact]
    public async Task StartJobAsync_保存入队用户并传递给后台同步服务()
    {
        var expected = new SyncResult
        {
            IsSuccess = true,
            Message = "完成",
        };
        var syncServiceMock = new Mock<IProductWarehouseReactService>(MockBehavior.Strict);
        syncServiceMock
            .Setup(service => service.SyncFromHqAsync("user-guid-1", "操作员甲"))
            .ReturnsAsync(expected);

        var service = CreateService(syncServiceMock);
        var started = await service.StartJobAsync(
            new WarehouseProductHqSyncJobRequestDto { OperationId = "sync-user" },
            "user-guid-1",
            "操作员甲"
        );

        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(WarehouseProductHqSyncJobStatusConstants.Succeeded, completed.Status);
        syncServiceMock.Verify(service => service.SyncFromHqAsync("user-guid-1", "操作员甲"), Times.Once);
    }

    private static WarehouseProductHqSyncJobService CreateService(
        Mock<IProductWarehouseReactService> syncServiceMock
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => syncServiceMock.Object);
        var provider = services.BuildServiceProvider();

        return new WarehouseProductHqSyncJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarehouseProductHqSyncJobService>.Instance
        );
    }

    private static async Task<WarehouseProductHqSyncJobDto> WaitForJobAsync(
        IWarehouseProductHqSyncJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current =>
                current?.Status == WarehouseProductHqSyncJobStatusConstants.Succeeded
                || current?.Status == WarehouseProductHqSyncJobStatusConstants.Failed,
            describeLast: current => $"仓库商品同步 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }
}
