using BlazorApp.Shared.DTOs;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 仓库改零售价两个开关的统一语义：不传 = 都勾选；只有「直接改了分店价又不生成任务」才需要当下同步 HQ。
/// </summary>
public sealed class WarehousePriceSyncPolicyTests
{
    [Fact]
    public void 请求不传开关_视同都勾选()
    {
        var policy = WarehousePriceSyncPolicy.From(null, null);

        Assert.True(policy.SyncStoreRetailPrices);
        Assert.True(policy.GenerateTasks);
        Assert.Equal(WarehousePriceSyncPolicy.Default, policy);
    }

    [Theory]
    // 同步 + 生成：直接改价并发待换标签，HQ 随分店执行任务再同步
    [InlineData(true, true, false)]
    // 不同步 + 生成：不动分店价，发改价格任务，HQ 随店员改价同步
    [InlineData(false, true, false)]
    // 同步 + 不生成：直接改价、无任务可等，改价当下同步 HQ
    [InlineData(true, false, true)]
    // 不同步 + 不生成：只改仓库价，没有分店价变动，也就没有可同步的
    [InlineData(false, false, false)]
    public void 四种组合_只有同步且不生成任务时才当下同步HQ(bool sync, bool tasks, bool expectedImmediateHq)
    {
        var policy = WarehousePriceSyncPolicy.From(sync, tasks);

        Assert.Equal(expectedImmediateHq, policy.ShouldSyncHqImmediately);
    }

    [Fact]
    public void 批量选项_非默认开关必须走带选项的重载()
    {
        Assert.False(new WarehouseProductBatchUpdateOptionsDto().UsesNonDefaultPriceSync);
        Assert.False(new WarehouseProductBatchUpdateOptionsDto { SyncStoreRetailPrices = true, GenerateTasks = true }.UsesNonDefaultPriceSync);
        Assert.True(new WarehouseProductBatchUpdateOptionsDto { SyncStoreRetailPrices = false }.UsesNonDefaultPriceSync);
        Assert.True(new WarehouseProductBatchUpdateOptionsDto { GenerateTasks = false }.UsesNonDefaultPriceSync);
    }
}
