using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ReactContainerControllerConcurrencyContractTests
{
    [Fact]
    public void 批量分类必须同时要求货柜编辑和POS商品管理权限()
    {
        var method = typeof(ReactContainerController).GetMethod(
            nameof(ReactContainerController.AssignCategoryByScope)
        );

        Assert.NotNull(method);
        var policies = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .ToList();

        Assert.Contains(Permissions.Container.Edit, policies);
        Assert.Contains(Permissions.PosProducts.Manage, policies);
    }

    [Fact]
    public async Task 批量预览权限必须按操作匹配_删除用户无需货柜编辑权限()
    {
        var service = new Mock<IContainerReactService>();
        service
            .Setup(item => item.PreviewBatchActionAsync("C-1", It.IsAny<ContainerDetailBatchPreviewRequestDto>()))
            .ReturnsAsync(new ContainerDetailBatchPreviewResultDto());
        var authorization = CreateAuthorizationService(Permissions.Container.Delete);
        var controller = CreateController(service.Object, authorization.Object);

        var result = await controller.PreviewContainerDetailBatchAction(
            "C-1",
            new ContainerDetailBatchPreviewRequestDto { Operation = "delete-details" }
        );

        Assert.IsType<OkObjectResult>(result);
        authorization.Verify(item => item.AuthorizeAsync(
            It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
            null,
            Permissions.Container.Delete
        ), Times.Once);
        authorization.Verify(item => item.AuthorizeAsync(
            It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
            null,
            Permissions.Container.Edit
        ), Times.Never);
    }

    [Fact]
    public async Task 批量分类预览必须同时通过货柜编辑和POS商品管理权限()
    {
        var service = new Mock<IContainerReactService>(MockBehavior.Strict);
        var authorization = CreateAuthorizationService(Permissions.Container.Edit);
        var controller = CreateController(service.Object, authorization.Object);

        var result = await controller.PreviewContainerDetailBatchAction(
            "C-1",
            new ContainerDetailBatchPreviewRequestDto { Operation = "assign-category" }
        );

        Assert.IsType<ForbidResult>(result);
        authorization.Verify(item => item.AuthorizeAsync(
            It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
            null,
            Permissions.PosProducts.Manage
        ), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 批量预览过期_所有新版执行入口均返回稳定409错误码()
    {
        var service = new Mock<IContainerReactService>();
        service
            .Setup(item => item.ApplyFloatRateByScopeAsync("C-1", It.IsAny<ContainerDetailApplyFloatRateRequestDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        service
            .Setup(item => item.ApplyPricesByScopeAsync("C-1", It.IsAny<ContainerDetailApplyPricesRequestDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        service
            .Setup(item => item.RecalculateCostsByScopeAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        service
            .Setup(item => item.BackfillLastPricesByScopeAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        service
            .Setup(item => item.BatchDeleteDetailsScopedAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        service
            .Setup(item => item.SetStatusByScopeAsync("C-1", It.IsAny<ContainerDetailSetStatusRequestDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        service
            .Setup(item => item.AssignCategoryByScopeAsync("C-1", It.IsAny<ContainerDetailAssignCategoryRequestDto>()))
            .ThrowsAsync(new ContainerDetailBatchPreviewConflictException("预览已过期"));
        var controller = CreateController(service.Object);

        var responses = new IActionResult[]
        {
            await controller.ApplyFloatRateByScope("C-1", new ContainerDetailApplyFloatRateRequestDto { FloatRate = 1.3m }),
            await controller.ApplyPricesByScope("C-1", new ContainerDetailApplyPricesRequestDto { ImportPrice = 1m }),
            await controller.RecalculateCostsByScope("C-1", new ContainerDetailBatchScopeDto()),
            await controller.BackfillLastPricesByScope("C-1", new ContainerDetailBatchScopeDto()),
            await controller.BatchDeleteDetailsScoped("C-1", new ContainerDetailBatchScopeDto()),
            await controller.SetStatusByScope("C-1", new ContainerDetailSetStatusRequestDto { IsActive = true }),
            await controller.AssignCategoryByScope("C-1", new ContainerDetailAssignCategoryRequestDto()),
        };

        foreach (var response in responses)
        {
            var conflict = Assert.IsType<ConflictObjectResult>(response);
            Assert.Equal(
                ContainerDetailBatchPreviewConflictException.ErrorCode,
                ReadProperty<string>(conflict.Value!, "code")
            );
        }
    }

    [Fact]
    public async Task 套装子项成本锁繁忙_所有批量执行入口返回409友好提示而非500()
    {
        // 生产 10-08：半点统计持有商品成本锁，批量改价等锁超时被兜底 catch 成 500「服务器内部错误」。
        var busy = new SetChildPurchasePriceLockException("HB:SetChildPurchasePrice:Product:A", -1);
        var service = new Mock<IContainerReactService>();
        service
            .Setup(item => item.ApplyFloatRateByScopeAsync("C-1", It.IsAny<ContainerDetailApplyFloatRateRequestDto>()))
            .ThrowsAsync(busy);
        service
            .Setup(item => item.ApplyPricesByScopeAsync("C-1", It.IsAny<ContainerDetailApplyPricesRequestDto>()))
            .ThrowsAsync(busy);
        service
            .Setup(item => item.RecalculateCostsByScopeAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ThrowsAsync(busy);
        service
            .Setup(item => item.BackfillLastPricesByScopeAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ThrowsAsync(busy);
        service
            .Setup(item => item.BatchDeleteDetailsScopedAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ThrowsAsync(busy);
        service
            .Setup(item => item.SetStatusByScopeAsync("C-1", It.IsAny<ContainerDetailSetStatusRequestDto>()))
            .ThrowsAsync(busy);
        // 带 SQL 死锁包装的锁异常也应提示成本锁繁忙，而不是“同一货柜正在保存”。
        service
            .Setup(item => item.AssignCategoryByScopeAsync("C-1", It.IsAny<ContainerDetailAssignCategoryRequestDto>()))
            .ThrowsAsync(new SetChildPurchasePriceLockException(
                "HB:SetChildPurchasePrice:Product:A",
                -3,
                new InvalidOperationException("模拟 SqlException 1205 包装")
            ));
        var controller = CreateController(service.Object);

        var responses = new IActionResult[]
        {
            await controller.ApplyFloatRateByScope("C-1", new ContainerDetailApplyFloatRateRequestDto { FloatRate = 1.3m }),
            await controller.ApplyPricesByScope("C-1", new ContainerDetailApplyPricesRequestDto { ImportPrice = 1m }),
            await controller.RecalculateCostsByScope("C-1", new ContainerDetailBatchScopeDto()),
            await controller.BackfillLastPricesByScope("C-1", new ContainerDetailBatchScopeDto()),
            await controller.BatchDeleteDetailsScoped("C-1", new ContainerDetailBatchScopeDto()),
            await controller.SetStatusByScope("C-1", new ContainerDetailSetStatusRequestDto { IsActive = true }),
            await controller.AssignCategoryByScope("C-1", new ContainerDetailAssignCategoryRequestDto()),
        };

        foreach (var response in responses)
        {
            var result = Assert.IsAssignableFrom<ObjectResult>(response);
            Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
            Assert.Equal(
                SetChildPurchasePriceMutationLock.BusyErrorCode,
                ReadProperty<string>(result.Value!, "code")
            );
            var message = ReadProperty<string>(result.Value!, "message");
            Assert.Contains("请约 1 分钟后再试", message);
            Assert.DoesNotContain("服务器内部错误", message);
        }
    }

    [Fact]
    public async Task 旧版明细写入缺少字段令牌_返回稳定428错误码()
    {
        var service = new Mock<IContainerReactService>();
        service
            .Setup(item => item.BatchUpdateDetailsDetailedAsync(It.IsAny<List<UpdateContainerDetailDto>>()))
            .ThrowsAsync(new ContainerDetailConcurrencyTokenRequiredException());
        var controller = CreateController(service.Object);

        var response = await controller.BatchUpdateDetails(
            new List<UpdateContainerDetailDto> { new() { HGUID = "D-1", 进口价格 = 1m } }
        );

        var required = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, required.StatusCode);
        Assert.Equal(
            ContainerDetailConcurrencyTokenRequiredException.ErrorCode,
            ReadProperty<string>(required.Value!, "code")
        );
    }

    [Fact]
    public async Task 删除明细成功响应必须同时返回删除数与范围内请求数()
    {
        // 前端 deleteContainerDetailsByScope 要求 totalDeleted 与 totalRequested 都是整数，
        // 缺 totalRequested 会在数据已物理删除后抛「返回数据不完整」，确认框不关、列表不更新。
        var service = new Mock<IContainerReactService>();
        service
            .Setup(item => item.BatchDeleteDetailsScopedAsync("C-1", It.IsAny<ContainerDetailBatchScopeDto>()))
            .ReturnsAsync((TotalDeleted: 2, TotalRequested: 3));
        var controller = CreateController(service.Object);

        var result = await controller.BatchDeleteDetailsScoped("C-1", new ContainerDetailBatchScopeDto());

        var ok = Assert.IsType<OkObjectResult>(result);
        // data 是匿名类型，不能用按精确类型断言的 ReadProperty<object> 读取
        var data = ok.Value!.GetType().GetProperty("data")?.GetValue(ok.Value);
        Assert.NotNull(data);
        Assert.Equal(2, ReadProperty<int>(data!, "totalDeleted"));
        Assert.Equal(3, ReadProperty<int>(data!, "totalRequested"));
    }

    private static ReactContainerController CreateController(
        IContainerReactService service,
        IAuthorizationService? authorizationService = null
    )
    {
        var controller = new ReactContainerController(
            service,
            Mock.Of<IContainerAllocationSalesReportService>(),
            Mock.Of<IContainerHqSyncService>(),
            new ContainerExportService(NullLogger<ContainerExportService>.Instance, new HttpClient()),
            authorizationService ?? Mock.Of<IAuthorizationService>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ReactContainerController>.Instance
        );
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static Mock<IAuthorizationService> CreateAuthorizationService(
        params string[] allowedPermissions
    )
    {
        var allowed = allowedPermissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(item => item.AuthorizeAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<object?>(),
                It.IsAny<string>()
            ))
            .ReturnsAsync((
                System.Security.Claims.ClaimsPrincipal _,
                object? _,
                string policy
            ) => allowed.Contains(policy)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());
        return authorization;
    }

    private static T ReadProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(value));
    }
}
