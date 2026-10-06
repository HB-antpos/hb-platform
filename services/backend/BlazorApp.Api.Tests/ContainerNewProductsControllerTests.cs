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
        service.Setup(x => x.GetAsync("S-1", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContainerNewProductsStateUnknownException());

        var response = await new ContainerNewProductsController(service.Object).Get("S-1", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains("STORE_STATE_UNKNOWN", System.Text.Json.JsonSerializer.Serialize(badRequest.Value));
    }

    [Fact]
    public async Task ForbiddenStore_Returns403()
    {
        var service = new Mock<IContainerNewProductsReactService>();
        service.Setup(x => x.GetAsync("S-2", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContainerNewProductsForbiddenException());

        var response = await new ContainerNewProductsController(service.Object).Get("S-2", CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncludeExisting_原样透传给服务(bool includeExisting)
    {
        var service = new Mock<IContainerNewProductsReactService>();
        service.Setup(x => x.GetAsync("S-1", includeExisting, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerNewProductsResponseDto { StoreCode = "S-1" });

        var response = await new ContainerNewProductsController(service.Object).Get("S-1", CancellationToken.None, includeExisting);

        Assert.IsType<OkObjectResult>(response);
        service.Verify(x => x.GetAsync("S-1", includeExisting, It.IsAny<CancellationToken>()), Times.Once);
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
        // 逐日枚举（覆盖一周内每个星期几起算）：凡到店区间与窗口有交集的货柜日期，都必须在粗筛窗口内，否则会漏柜
        for (var today = new DateTime(2026, 9, 21); today < new DateTime(2026, 10, 5); today = today.AddDays(1))
        {
            var (from, toExclusive) = ContainerNewProductsReactService.BuildWindow(today);
            var (containerFrom, containerToExclusive) = ContainerNewProductsReactService.BuildContainerQueryWindow(from, toExclusive);
            for (var containerDate = today.AddDays(-40); containerDate < today.AddDays(40); containerDate = containerDate.AddDays(1))
            {
                foreach (var state in new[] { "NSW", "QLD" })
                {
                    var (startWeekdays, endWeekdays) = ContainerNewProductsReactService.GetStoreArrivalWeekdayRange(state);
                    var start = ContainerNewProductsReactService.AddWeekdays(containerDate, startWeekdays);
                    var end = ContainerNewProductsReactService.AddWeekdays(containerDate, endWeekdays);
                    if (ContainerNewProductsReactService.OverlapsWindow(start, end, from, toExclusive))
                    {
                        Assert.InRange(containerDate, containerFrom, containerToExclusive.AddDays(-1));
                    }
                }
            }
        }
    }

    [Fact]
    public void QueryWindow_UsesInclusiveSevenBackAndTwentyOneForwardDays()
    {
        var (from, toExclusive) = ContainerNewProductsReactService.BuildWindow(new DateTime(2026, 9, 28));

        Assert.Equal(new DateTime(2026, 9, 21), from);
        Assert.Equal(new DateTime(2026, 10, 20), toExclusive);
        Assert.True(new DateTime(2026, 10, 19) < toExclusive);
        Assert.False(new DateTime(2026, 10, 20) < toExclusive);
    }

    [Theory]
    [InlineData("NSW", 0, 3)]
    [InlineData("QLD", 3, 7)]
    public void StoreArrivalWeekdayRange_NswSameDayToThree_QldThreeToSeven(string state, int start, int end)
    {
        Assert.Equal((start, end), ContainerNewProductsReactService.GetStoreArrivalWeekdayRange(state));
    }

    [Theory]
    // 窗口 [09-21, 10-13)：区间只要有一天落在窗口内就算
    [InlineData("2026-09-17", "2026-09-20", false)]
    [InlineData("2026-09-17", "2026-09-21", true)]
    [InlineData("2026-10-12", "2026-10-15", true)]
    [InlineData("2026-10-13", "2026-10-16", false)]
    public void OverlapsWindow_IncludesRangeCrossingWindowEdge(string start, string end, bool expected)
    {
        Assert.Equal(expected, ContainerNewProductsReactService.OverlapsWindow(
            DateTime.Parse(start), DateTime.Parse(end), new DateTime(2026, 9, 21), new DateTime(2026, 10, 13)));
    }

    [Theory]
    [InlineData("2026-09-18", 0, "2026-09-18")]
    [InlineData("2026-09-19", 0, "2026-09-19")]
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

    [Theory]
    // 生产 Bankstown：地址只写了区名，但分店管理配置了 Sydney 时区
    [InlineData("Australia/Sydney", "Bankstown", "NSW")]
    [InlineData("Australia/Brisbane", "Shopping Centre", "QLD")]
    // 配置的时区优先于地址
    [InlineData("Australia/Brisbane", "SHOP 1 65-69 CRONULLA ST, CRONULLA, NSW, 2230", "QLD")]
    // 无效或本功能不支持的时区回退到地址推导
    [InlineData("Invalid/TimeZone", "10 Main Street, Brisbane QLD 4000", "QLD")]
    [InlineData("Australia/Melbourne", "Shop 1, Sydney NSW 2000", "NSW")]
    [InlineData(null, "10 Main Street, Brisbane QLD 4000", "QLD")]
    public void ResolveState_PrefersConfiguredTimeZoneThenAddress(string? timeZoneId, string address, string expected)
    {
        Assert.Equal(expected, ContainerNewProductsReactService.ResolveState(new Store { TimeZoneId = timeZoneId, Address = address }));
    }

    [Theory]
    [InlineData("Australia/Melbourne", "Shop 1, Cheltenham 3192, VIC")]
    [InlineData(null, "Bankstown")]
    public void ResolveState_ReturnsNullWhenNeitherTimeZoneNorAddressGivesSupportedState(string? timeZoneId, string address)
    {
        Assert.Null(ContainerNewProductsReactService.ResolveState(new Store { TimeZoneId = timeZoneId, Address = address }));
    }
}
