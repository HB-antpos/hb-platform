using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductPushToHqJobServiceTests
{
    [Fact]
    public async Task StartJobAsync_立即返回运行中任务并在完成后保留统计()
    {
        var releasePush = new TaskCompletionSource<ApiResponse<PushProductsToHqResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var pushService = new Mock<IProductHqSyncService>();
        pushService
            .Setup(service => service.PushToHqAsync(It.Is<PushProductsToHqRequest>(request =>
                request.Items.Count == 1 && request.Items[0].ProductCode == "HB001"
            )))
            .Returns(releasePush.Task);
        var service = CreateService(pushService);

        var started = await service.StartJobAsync(new PushProductsToHqRequest
        {
            Items = new List<PushProductsToHqItem>
            {
                new() { ProductCode = "HB001" },
            },
        });

        Assert.Equal(ProductPushToHqJobStatusConstants.Running, started.Status);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));

        releasePush.SetResult(ApiResponse<PushProductsToHqResult>.OK(
            new PushProductsToHqResult
            {
                ProductsAdded = 1,
                StoreRetailPricesCreated = 2,
                ProductSetCodesCreated = 1,
                StoreMultiCodesCreated = 2,
                WarehouseInventoriesCreated = 1,
                SuccessCount = 1,
                TotalCount = 1,
            },
            "商品推送HQ完成"
        ));

        var completed = await WaitForJobAsync(service, started.JobId);
        Assert.Equal(ProductPushToHqJobStatusConstants.Succeeded, completed.Status);
        Assert.NotNull(completed.Result);
        Assert.Equal(1, completed.Result!.ProductsAdded);
        Assert.Equal(2, completed.Result.StoreRetailPricesCreated);
        Assert.Equal(1, completed.Result.ProductSetCodesCreated);
        Assert.Equal(2, completed.Result.StoreMultiCodesCreated);
        Assert.Equal(1, completed.Result.WarehouseInventoriesCreated);
        Assert.Equal("商品推送HQ完成", completed.Message);
        pushService.VerifyAll();
    }

    [Fact]
    public async Task StartJobAsync_推送服务业务失败时状态为Failed并保留错误明细()
    {
        var pushService = new Mock<IProductHqSyncService>();
        pushService
            .Setup(service => service.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .ReturnsAsync(ApiResponse<PushProductsToHqResult>.Error(
                "推送候选包含错误，未写入HQ",
                "PRODUCT_HQ_PUSH_ITEM_ERRORS",
                new PushProductsToHqResult
                {
                    FailedCount = 1,
                    TotalCount = 1,
                    Errors = new List<string> { "商品不存在或已删除: HB404" },
                }
            ));
        var service = CreateService(pushService);

        var started = await service.StartJobAsync(new PushProductsToHqRequest
        {
            Items = new List<PushProductsToHqItem>
            {
                new() { ProductCode = "HB404" },
            },
        });
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(ProductPushToHqJobStatusConstants.Failed, completed.Status);
        Assert.NotNull(completed.Result);
        Assert.Equal(1, completed.Result!.FailedCount);
        Assert.Contains(completed.Result.Errors, item => item.Contains("HB404"));
        Assert.Equal("推送候选包含错误，未写入HQ", completed.Message);
    }

    [Fact]
    public async Task StartJobAsync_套装成本锁冲突时Failed任务保留BUSY错误码()
    {
        var pushService = new Mock<IProductHqSyncService>();
        pushService
            .Setup(service => service.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .ReturnsAsync(ApiResponse<PushProductsToHqResult>.Error(
                "套装商品正在被其他操作修改，请稍后重试",
                "SET_CHILD_PURCHASE_PRICE_BUSY",
                new PushProductsToHqResult
                {
                    FailedCount = 1,
                    TotalCount = 1,
                }
            ));
        var service = CreateService(pushService);

        var started = await service.StartJobAsync(new PushProductsToHqRequest
        {
            ProductCodes = new List<string> { "HB-BUSY" },
        });
        var completed = await WaitForJobAsync(service, started.JobId);

        Assert.Equal(ProductPushToHqJobStatusConstants.Failed, completed.Status);
        Assert.Equal("SET_CHILD_PURCHASE_PRICE_BUSY", completed.ErrorCode);
    }

    [Fact]
    public async Task StartJobAsync_连续提交不同推送请求_不会复用运行中Job()
    {
        var releasePush = new TaskCompletionSource<ApiResponse<PushProductsToHqResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var pushService = new Mock<IProductHqSyncService>();
        pushService
            .Setup(service => service.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .Returns(releasePush.Task);
        var service = CreateService(pushService);

        var first = await service.StartJobAsync(new PushProductsToHqRequest
        {
            Items = new List<PushProductsToHqItem> { new() { ProductCode = "HB001" } },
        });
        var second = await service.StartJobAsync(new PushProductsToHqRequest
        {
            Items = new List<PushProductsToHqItem> { new() { ProductCode = "HB002" } },
        });

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.False(second.IsDuplicateRequest);

        releasePush.SetResult(ApiResponse<PushProductsToHqResult>.OK(
            new PushProductsToHqResult(),
            "完成"
        ));
        await WaitForJobAsync(service, first.JobId);
        await WaitForJobAsync(service, second.JobId);
    }

    [Fact]
    public async Task StartJobAsync_保留指定分店范围透传给推送服务()
    {
        var pushService = new Mock<IProductHqSyncService>();
        pushService
            .Setup(service => service.PushToHqAsync(It.Is<PushProductsToHqRequest>(request =>
                request.TargetStoreCodes != null
                && request.TargetStoreCodes.SequenceEqual(new[] { "S01", "S02" })
            )))
            .ReturnsAsync(
                ApiResponse<PushProductsToHqResult>.OK(
                    new PushProductsToHqResult
                    {
                        SuccessCount = 1,
                        TotalCount = 1,
                    },
                    "商品推送HQ完成"
                )
            );
        var service = CreateService(pushService);

        var started = await service.StartJobAsync(new PushProductsToHqRequest
        {
            ProductCodes = new List<string> { "HB001" },
            TargetStoreCodes = new List<string> { "S01", "S02" },
        });

        var completed = await WaitForJobAsync(service, started.JobId);
        Assert.Equal(ProductPushToHqJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(1, completed.Result!.SuccessCount);
        pushService.VerifyAll();
    }

    private static ProductPushToHqJobService CreateService(Mock<IProductHqSyncService> pushService)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => pushService.Object);
        var provider = services.BuildServiceProvider();

        return new ProductPushToHqJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductPushToHqJobService>.Instance
        );
    }

    private static async Task<PushProductsToHqJobDto> WaitForJobAsync(
        IProductPushToHqJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current =>
                current?.Status == ProductPushToHqJobStatusConstants.Succeeded
                || current?.Status == ProductPushToHqJobStatusConstants.Failed,
            describeLast: current => $"商品推送 HQ job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }
}
