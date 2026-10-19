using System.Security.Claims;
using System.Text.Json;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public class ContainerNumberMaskingTests
{
    [Theory]
    [InlineData("FFAU8228793", "*******8793")]
    [InlineData("12345", "*2345")]
    [InlineData("8793", "8793")]
    [InlineData("AB", "AB")]
    [InlineData("", "")]
    [InlineData(null, null)]
    // 已遮罩的值再遮罩不变，前端二次遮罩也安全。
    [InlineData("*******8793", "*******8793")]
    public void Mask_只保留后4位(string? input, string? expected)
    {
        Assert.Equal(expected, ContainerNumberMasking.Mask(input));
    }

    [Fact]
    public void ToFileNamePart_去掉遮罩字符()
    {
        Assert.Equal("8793", ContainerNumberMasking.ToFileNamePart("*******8793"));
        Assert.Equal("FFAU8228793", ContainerNumberMasking.ToFileNamePart("FFAU8228793"));
    }

    [Fact]
    public void JsonConverter_作用域外原样输出_作用域内遮罩_且不修改对象()
    {
        var dto = new ContainerMainDto { HGUID = "G", 货柜编号 = "FFAU8228793" };

        var raw = JsonSerializer.Serialize(dto);
        string masked;
        using (ContainerNumberMasking.BeginMaskingScope(mask: true))
        {
            masked = JsonSerializer.Serialize(dto);
        }
        var afterScope = JsonSerializer.Serialize(dto);

        Assert.Contains("FFAU8228793", raw);
        Assert.Contains("*******8793", masked);
        Assert.DoesNotContain("FFAU8228793", masked);
        Assert.Contains("FFAU8228793", afterScope);
        // 缓存里的共享对象不能被改写。
        Assert.Equal("FFAU8228793", dto.货柜编号);
        // 读取方向原样。
        Assert.Equal("FFAU8228793", JsonSerializer.Deserialize<ContainerMainDto>(raw)!.货柜编号);
    }

    [Fact]
    public void JsonConverter_覆盖所有返回货柜编号的DTO()
    {
        using var scope = ContainerNumberMasking.BeginMaskingScope(mask: true);
        object[] payloads =
        {
            new ContainerMainDto { 货柜编号 = "FFAU8228793" },
            new ComingSoonContainerDto { 货柜编号 = "FFAU8228793" },
            new YiwuContainerDto { ContainerNumber = "FFAU8228793" },
            new ContainerAllocationSalesReportResponse { ContainerNumber = "FFAU8228793" },
            new ContainerNewProductItemDto { ContainerNumber = "FFAU8228793" },
            new WarehouseProductRecordContainerItemDto { ContainerNumber = "FFAU8228793" },
            new WarehouseProductFlowContainerDto { ContainerNumber = "FFAU8228793" },
            new WarehouseProductInsightContainerDto { ContainerNumber = "FFAU8228793" },
            new StoreOrderImportPriceVarianceItemDto { FirstContainerNumber = "FFAU8228793" },
            new StoreOrderImportPriceVarianceDetailItemDto { FirstContainerNumber = "FFAU8228793" },
        };

        foreach (var payload in payloads)
        {
            var json = JsonSerializer.Serialize(payload, payload.GetType());
            Assert.DoesNotContain("FFAU8228793", json);
            Assert.Contains("*******8793", json);
        }
    }

    [Theory]
    [InlineData(false, "*******8793")]
    [InlineData(true, "FFAU8228793")]
    public async Task MaskContainerNumbersAttribute_按权限决定响应序列化是否遮罩(
        bool hasPermission,
        string expected
    )
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                Permissions.Container.ViewFullNumber
            ))
            .ReturnsAsync(hasPermission ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var services = new ServiceCollection()
            .AddSingleton(authorization.Object)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var payload = new { success = true, data = new ContainerMainDto { 货柜编号 = "FFAU8228793" } };
        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new OkObjectResult(payload),
            controller: new object()
        );
        string? serialized = null;

        await new MaskContainerNumbersAttribute().OnResultExecutionAsync(
            context,
            () =>
            {
                // 模拟输出格式化器在 next() 中序列化响应。
                serialized = JsonSerializer.Serialize(payload);
                return Task.FromResult(
                    new ResultExecutedContext(context, context.Filters, context.Result, context.Controller)
                );
            }
        );

        Assert.NotNull(serialized);
        Assert.Contains(expected, serialized);
        // 过滤器结束后作用域恢复，不影响同一线程后续的内部序列化。
        Assert.False(ContainerNumberMasking.IsMaskingActive);
    }

    [Fact]
    public async Task CanViewFullContainerNumberAsync_拿不到授权服务时按无权限处理()
    {
        Assert.False(await ((HttpContext?)null).CanViewFullContainerNumberAsync());
        Assert.False(await new DefaultHttpContext().CanViewFullContainerNumberAsync());
    }

    [Theory]
    [InlineData("FFAU8228793", "货柜明细_FFAU8228793")]
    [InlineData("*******8793", "货柜明细_8793")]
    [InlineData("A/B:C", "货柜明细_A_B_C")]
    [InlineData(null, "货柜明细")]
    public void BuildWorksheetName_去掉工作表名非法字符(string? number, string expected)
    {
        Assert.Equal(expected, ContainerExportService.BuildWorksheetName(number));
    }

    [Fact]
    public void 新权限码_登记在种子与仓库管理角色模板()
    {
        Assert.Contains(
            PermissionSeedData.AllPermissions,
            seed => seed.Code == Permissions.Container.ViewFullNumber
        );
        var warehouseManager = Assert.Single(
            PermissionSeedData.RolePermissionTemplates,
            template => template.RoleName == "WarehouseManager"
        );
        Assert.Contains(Permissions.Container.ViewFullNumber, warehouseManager.PermissionCodes);
        var warehouseStaff = Assert.Single(
            PermissionSeedData.RolePermissionTemplates,
            template => template.RoleName == "WarehouseStaff"
        );
        Assert.DoesNotContain(Permissions.Container.ViewFullNumber, warehouseStaff.PermissionCodes);
    }
}
