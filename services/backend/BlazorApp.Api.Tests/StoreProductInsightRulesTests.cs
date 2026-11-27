using BlazorApp.Api.Features.ProductInsights;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreProductInsightRulesTests
{
    [Fact]
    public void CanAccessStore_限制登录用户和设备不能跨店读取()
    {
        Assert.True(StoreProductInsightRules.CanAccessStore(null, "S001"));
        Assert.True(StoreProductInsightRules.CanAccessStore(["S001", "S002"], "S002"));
        Assert.False(StoreProductInsightRules.CanAccessStore(["S001"], "S002"));
    }

    [Fact]
    public void IsValidRange_拒绝反向日期边界()
    {
        Assert.True(StoreProductInsightRules.IsValidRange(new DateTime(2026, 9, 14), new DateTime(2026, 9, 14)));
        Assert.False(StoreProductInsightRules.IsValidRange(new DateTime(2026, 9, 15), new DateTime(2026, 9, 14)));
    }

    [Fact]
    public void 默认区间天数_旧客户端仍为90天且days限定在1到366()
    {
        Assert.Equal(90, StoreProductInsightRules.DefaultRangeDays);
        Assert.True(StoreProductInsightRules.IsValidDefaultRangeDays(1));
        Assert.True(StoreProductInsightRules.IsValidDefaultRangeDays(180));
        Assert.True(StoreProductInsightRules.IsValidDefaultRangeDays(366));
        Assert.False(StoreProductInsightRules.IsValidDefaultRangeDays(0));
        Assert.False(StoreProductInsightRules.IsValidDefaultRangeDays(367));
    }

    [Theory]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    public void IsValidLocalPurchase_未删除且至少有一个进货业务日期(
        bool isInvoiceDeleted, bool isDetailDeleted, bool hasInboundDate, bool hasOrderDate, bool expected)
    {
        var actual = StoreProductInsightRules.IsValidLocalPurchase(
            isInvoiceDeleted: isInvoiceDeleted,
            isDetailDeleted: isDetailDeleted,
            inboundDate: hasInboundDate ? new DateTime(2026, 9, 14) : null,
            orderDate: hasOrderDate ? new DateTime(2026, 9, 13) : null
        );

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsWarehouseDelivery_按出库日含首尾且排除未完成配货()
    {
        var start = new DateTime(2026, 6, 17);
        var end = new DateTime(2026, 9, 14);

        Assert.True(StoreProductInsightRules.IsWarehouseDelivery(2, start, 3, start, end));
        Assert.True(StoreProductInsightRules.IsWarehouseDelivery(2, end.AddHours(23), 3, start, end));
        Assert.False(StoreProductInsightRules.IsWarehouseDelivery(3, end, 3, start, end));
        Assert.False(StoreProductInsightRules.IsWarehouseDelivery(2, end, 0, start, end));
        Assert.False(StoreProductInsightRules.IsWarehouseDelivery(2, end.AddDays(1), 3, start, end));
    }

    [Fact]
    public void IsHistoricalRecordEligible_不让最近历史穿过查询结束日()
    {
        var end = new DateTime(2026, 9, 14);

        Assert.True(StoreProductInsightRules.IsHistoricalRecordEligible(end, end));
        Assert.False(StoreProductInsightRules.IsHistoricalRecordEligible(end.AddDays(1), end));
    }
}
