using System.Collections.Concurrent;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>列表批量商品检测后台任务：顺序、跳过、冲突、停止、排队。</summary>
public sealed class LocalSupplierInvoiceBatchCheckProductsJobTests
{
    [Fact]
    public async Task 按提交顺序逐张检测_排除已执行明细_并汇总成功跳过失败()
    {
        var calls = new ConcurrentQueue<CheckProductsRequest>();
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.IsAny<CheckProductsRequest>()))
            .Returns((CheckProductsRequest request) =>
            {
                calls.Enqueue(request);
                return Task.FromResult(request.InvoiceGuid switch
                {
                    "inv-ok" => ApiResponse<CheckProductsResponseDto>.OK(Checked(3)),
                    "inv-all-executed" => ApiResponse<CheckProductsResponseDto>.OK(Checked(0)),
                    "inv-missing" => ApiResponse<CheckProductsResponseDto>.Error("订单不存在", "NOT_FOUND"),
                    _ => throw new InvalidOperationException("数据库超时"),
                });
            });
        var service = CreateService(storeService);

        var started = await service.StartBatchCheckProductsJobAsync(
            ["inv-ok", "inv-all-executed", "inv-missing", "inv-throws"],
            ["1005"]
        );
        var completed = await WaitForBatchAsync(service, started.JobId);

        Assert.Equal(["inv-ok", "inv-all-executed", "inv-missing", "inv-throws"], calls.Select(c => c.InvoiceGuid));
        Assert.All(calls, request =>
        {
            Assert.True(request.ExcludeExecutedDetails);
            Assert.Null(request.DetailGuids);
        });
        Assert.Equal(LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Completed, completed.Status);
        Assert.Equal(4, completed.Processed);
        Assert.Equal(1, completed.Succeeded);
        Assert.Equal(1, completed.Skipped);
        Assert.Equal(2, completed.Failed);
        Assert.Equal(3, completed.Items[0].CheckedCount);
        Assert.Equal(LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped, completed.Items[1].Status);
        Assert.Equal("订单不存在", completed.Items[2].Message);
        Assert.Equal("数据库超时", completed.Items[3].Message);
        Assert.Equal(["1005"], completed.StoreCodes);
    }

    [Fact]
    public async Task 明细页单张检测运行中时_批量跳过该单()
    {
        var singleRelease = new TaskCompletionSource<ApiResponse<CheckProductsResponseDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => !r.ExcludeExecutedDetails)))
            .Returns(singleRelease.Task);
        storeService
            .Setup(service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => r.ExcludeExecutedDetails)))
            .ReturnsAsync(ApiResponse<CheckProductsResponseDto>.OK(Checked(2)));
        var service = CreateService(storeService);

        await service.StartCheckProductsJobAsync(new CheckProductsRequest { InvoiceGuid = "inv-busy" });
        var started = await service.StartBatchCheckProductsJobAsync(["inv-busy", "inv-free"], ["1005"]);
        var completed = await WaitForBatchAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped, completed.Items[0].Status);
        Assert.Equal("该单正在检测中", completed.Items[0].Message);
        Assert.Equal(LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Succeeded, completed.Items[1].Status);
        singleRelease.SetResult(ApiResponse<CheckProductsResponseDto>.OK(Checked(1)));
    }

    [Fact]
    public async Task 批量正在检测某单时_明细页对同单单张检测收到冲突()
    {
        var batchRelease = new TaskCompletionSource<ApiResponse<CheckProductsResponseDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var batchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.IsAny<CheckProductsRequest>()))
            .Returns(() =>
            {
                batchEntered.TrySetResult();
                return batchRelease.Task;
            });
        var service = CreateService(storeService);

        var started = await service.StartBatchCheckProductsJobAsync(["inv-1"], ["1005"]);
        await batchEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        await Assert.ThrowsAsync<LocalSupplierInvoiceBatchUpdateJobConflictException>(() =>
            service.StartCheckProductsJobAsync(new CheckProductsRequest { InvoiceGuid = "inv-1" })
        );

        batchRelease.SetResult(ApiResponse<CheckProductsResponseDto>.OK(Checked(1)));
        var completed = await WaitForBatchAsync(service, started.JobId);
        Assert.Equal(1, completed.Succeeded);

        // 批量完成后锁释放，单张检测可以正常提交。
        var single = await service.StartCheckProductsJobAsync(new CheckProductsRequest { InvoiceGuid = "inv-1" });
        Assert.False(string.IsNullOrWhiteSpace(single.JobId));
    }

    [Fact]
    public async Task 停止剩余_正在检测的跑完_排队中的标记跳过()
    {
        var firstRelease = new TaskCompletionSource<ApiResponse<CheckProductsResponseDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => r.InvoiceGuid == "inv-1")))
            .Returns(() =>
            {
                firstEntered.TrySetResult();
                return firstRelease.Task;
            });
        var service = CreateService(storeService);

        var started = await service.StartBatchCheckProductsJobAsync(["inv-1", "inv-2", "inv-3"], ["1005"]);
        await firstEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var cancelling = await service.CancelBatchCheckProductsJobAsync(started.JobId);
        Assert.True(cancelling!.CancelRequested);

        firstRelease.SetResult(ApiResponse<CheckProductsResponseDto>.OK(Checked(5)));
        var completed = await WaitForBatchAsync(service, started.JobId);

        Assert.Equal(LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Cancelled, completed.Status);
        Assert.Equal(LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Succeeded, completed.Items[0].Status);
        Assert.All(completed.Items.Skip(1), item =>
        {
            Assert.Equal(LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Skipped, item.Status);
            Assert.Equal("已停止", item.Message);
        });
        storeService.Verify(
            service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => r.InvoiceGuid != "inv-1")),
            Times.Never
        );
    }

    [Fact]
    public async Task 同一组进货单重复提交_复用运行中的任务()
    {
        var release = new TaskCompletionSource<ApiResponse<CheckProductsResponseDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.IsAny<CheckProductsRequest>()))
            .Returns(release.Task);
        var service = CreateService(storeService);

        var first = await service.StartBatchCheckProductsJobAsync(["inv-a", "inv-b"], ["1005"]);
        var duplicate = await service.StartBatchCheckProductsJobAsync(["inv-b", "inv-a"], ["1005"]);

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<CheckProductsResponseDto>.OK(Checked(1)));
        await WaitForBatchAsync(service, first.JobId);
    }

    [Fact]
    public async Task 多个批量任务排队串行_后提交的先等待()
    {
        var firstRelease = new TaskCompletionSource<ApiResponse<CheckProductsResponseDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeService = new Mock<ILocalSupplierInvoicesReactService>();
        storeService
            .Setup(service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => r.InvoiceGuid == "inv-1")))
            .Returns(() =>
            {
                firstEntered.TrySetResult();
                return firstRelease.Task;
            });
        storeService
            .Setup(service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => r.InvoiceGuid == "inv-2")))
            .ReturnsAsync(ApiResponse<CheckProductsResponseDto>.OK(Checked(1)));
        var service = CreateService(storeService);

        var first = await service.StartBatchCheckProductsJobAsync(["inv-1"], ["1005"]);
        await firstEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var second = await service.StartBatchCheckProductsJobAsync(["inv-2"], ["1005"]);

        var waiting = await service.GetBatchCheckProductsJobAsync(second.JobId);
        Assert.True(waiting!.IsWaiting);
        storeService.Verify(
            service => service.CheckProductsAsync(It.Is<CheckProductsRequest>(r => r.InvoiceGuid == "inv-2")),
            Times.Never
        );

        firstRelease.SetResult(ApiResponse<CheckProductsResponseDto>.OK(Checked(1)));
        await WaitForBatchAsync(service, first.JobId);
        var secondCompleted = await WaitForBatchAsync(service, second.JobId);
        Assert.False(secondCompleted.IsWaiting);
        Assert.Equal(1, secondCompleted.Succeeded);
    }

    [Fact]
    public async Task 超过单批上限时拒绝提交()
    {
        var service = CreateService(new Mock<ILocalSupplierInvoicesReactService>());
        var guids = Enumerable.Range(1, LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices + 1)
            .Select(index => $"inv-{index}")
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StartBatchCheckProductsJobAsync(guids, ["1005"])
        );
    }

    private static CheckProductsResponseDto Checked(int total)
    {
        return new CheckProductsResponseDto { Summary = new CheckProductsSummaryDto { Total = total } };
    }

    private static LocalSupplierInvoiceBatchUpdateJobService CreateService(
        Mock<ILocalSupplierInvoicesReactService> storeService
    )
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => storeService.Object);
        var provider = services.BuildServiceProvider();
        return new LocalSupplierInvoiceBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LocalSupplierInvoiceBatchUpdateJobService>.Instance
        );
    }

    private static async Task<LocalSupplierInvoiceBatchCheckProductsJobDto> WaitForBatchAsync(
        ILocalSupplierInvoiceBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await AsyncTestWaitSupport.WaitForValueAsync(
            () => service.GetBatchCheckProductsJobAsync(jobId),
            current => current?.Status is LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Completed
                or LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Cancelled,
            describeLast: current => $"批量商品检测 job 当前状态：{current?.Status ?? "未找到"}，已处理 {current?.Processed}"
        );
        return job!;
    }
}
