using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerNewProductsControllerTests
{
    [Fact]
    public async Task UnknownState_Returns400WithStableErrorCode()
    {
        var service = new Mock<IContainerNewProductsReactService>();
        service.Setup(x => x.GetAsync("S-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContainerNewProductsStateUnknownException());

        var response = await new ContainerNewProductsController(service.Object).Get("S-1", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains("STORE_STATE_UNKNOWN", System.Text.Json.JsonSerializer.Serialize(badRequest.Value));
    }

    [Fact]
    public async Task ForbiddenStore_Returns403()
    {
        var service = new Mock<IContainerNewProductsReactService>();
        service.Setup(x => x.GetAsync("S-2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContainerNewProductsForbiddenException());

        var response = await new ContainerNewProductsController(service.Object).Get("S-2", CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
    }

}

public sealed class ContainerNewProductsRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("P-001", true)]
    public void ProductCodeRule_RejectsNullEmptyAndWhitespace(string? productCode, bool expected)
    {
        Assert.Equal(expected, ContainerNewProductsReactService.HasUsableProductCode(productCode));
    }

    [Fact]
    public void ItemContract_KeepsInternalCodeAndBusinessContainerNumberSeparate()
    {
        var item = new ContainerNewProductItemDto { ContainerCode = "uuid-7", ContainerNumber = "OOCU1234567" };

        Assert.Equal("uuid-7", item.ContainerCode);
        Assert.Equal("OOCU1234567", item.ContainerNumber);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ProductRule_ExistingSoftDeletedRecordStillNeedsMatchingAudit(bool exists, bool audit, bool expected)
    {
        Assert.Equal(expected, ContainerNewProductsReactService.ShouldIncludeProduct(exists, audit));
    }

    [Fact]
    public void ContainerQueryWindow_CoversEveryContainerWhoseStoreArrivalFallsInWindow()
    {
        // 逐日枚举（覆盖一周内每个星期几起算）：凡到店日落在窗口内的货柜日期，都必须在粗筛窗口内，否则会漏柜
        for (var today = new DateTime(2026, 9, 21); today < new DateTime(2026, 10, 5); today = today.AddDays(1))
        {
            var (from, toExclusive) = ContainerNewProductsReactService.BuildWindow(today);
            var (containerFrom, containerToExclusive) = ContainerNewProductsReactService.BuildContainerQueryWindow(from, toExclusive);
            for (var containerDate = today.AddDays(-40); containerDate < today.AddDays(40); containerDate = containerDate.AddDays(1))
            {
                foreach (var weekdays in new[] { 3, 7 })
                {
                    var storeArrival = ContainerNewProductsReactService.AddWeekdays(containerDate, weekdays);
                    if (storeArrival >= from && storeArrival < toExclusive)
                    {
                        Assert.InRange(containerDate, containerFrom, containerToExclusive.AddDays(-1));
                    }
                }
            }
        }
    }

    [Fact]
    public void QueryWindow_UsesInclusiveSevenBackAndFourteenForwardDays()
    {
        var (from, toExclusive) = ContainerNewProductsReactService.BuildWindow(new DateTime(2026, 9, 28));

        Assert.Equal(new DateTime(2026, 9, 21), from);
        Assert.Equal(new DateTime(2026, 10, 13), toExclusive);
        Assert.True(new DateTime(2026, 10, 12) < toExclusive);
        Assert.False(new DateTime(2026, 10, 13) < toExclusive);
    }

    [Theory]
    [InlineData("2026-09-18", 3, "2026-09-23")]
    [InlineData("2026-09-18", 7, "2026-09-29")]
    [InlineData("2026-09-19", 3, "2026-09-23")]
    public void AddWeekdays_SkipsWeekend(string start, int days, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), ContainerNewProductsReactService.AddWeekdays(DateTime.Parse(start), days));
    }

    [Theory]
    [InlineData("10 Main Street, Sydney NSW 2000", "NSW")]
    [InlineData("10 Main Street, Brisbane QLD 4000", "QLD")]
    public void ResolveState_UsesExplicitStateOrPostcode(string address, string expected)
    {
        Assert.Equal(expected, ContainerNewProductsReactService.ResolveState(new Store { Address = address }));
    }
}
