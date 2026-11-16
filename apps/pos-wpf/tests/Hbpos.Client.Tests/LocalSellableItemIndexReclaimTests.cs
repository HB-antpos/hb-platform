using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 整表替换后的延迟回收：连续替换合并成一次、回收后再替换会重新排、回收动作出错不影响索引与后续回收。
/// 测试里把回收动作换成计数器，不会真的触发全堆回收。
/// </summary>
public sealed class LocalSellableItemIndexReclaimTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Rapid_replaces_are_coalesced_into_a_single_reclaim()
    {
        var reclaimed = new SemaphoreSlim(0);
        var count = 0;
        var index = new LocalSellableItemIndex
        {
            ReclaimDelay = TimeSpan.FromMilliseconds(200),
            ReclaimMemory = () =>
            {
                Interlocked.Increment(ref count);
                reclaimed.Release();
            },
        };

        index.ReplaceAll([CreateItem("A")]);
        index.ReplaceAll([CreateItem("B")]);
        index.ReplaceAll([CreateItem("C")]);

        Assert.True(await reclaimed.WaitAsync(Timeout));
        // 再留一点时间，确认没有第二次回收。
        await Task.Delay(500);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.Equal("C", Assert.Single(index.Items).ProductCode);
    }

    [Fact]
    public async Task Replace_after_a_finished_reclaim_schedules_another_one()
    {
        var reclaimed = new SemaphoreSlim(0);
        var index = new LocalSellableItemIndex
        {
            ReclaimDelay = TimeSpan.FromMilliseconds(50),
            ReclaimMemory = () => reclaimed.Release(),
        };

        index.ReplaceAll([CreateItem("A")]);
        Assert.True(await reclaimed.WaitAsync(Timeout));

        index.ReplaceAll([CreateItem("B")]);
        Assert.True(await reclaimed.WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_failing_reclaim_neither_breaks_the_index_nor_blocks_later_reclaims()
    {
        var attempts = new SemaphoreSlim(0);
        var failFirst = true;
        var index = new LocalSellableItemIndex
        {
            ReclaimDelay = TimeSpan.FromMilliseconds(50),
            ReclaimMemory = () =>
            {
                attempts.Release();
                if (failFirst)
                {
                    failFirst = false;
                    throw new InvalidOperationException("reclaim failed");
                }
            },
        };

        index.ReplaceAll([CreateItem("A")]);
        Assert.True(await attempts.WaitAsync(Timeout));
        Assert.Equal("A", Assert.Single(index.FindExactMatches("S001", "CODE-A")).ProductCode);

        index.ReplaceAll([CreateItem("B")]);
        Assert.True(await attempts.WaitAsync(Timeout));
        Assert.Equal("B", Assert.Single(index.FindExactMatches("S001", "CODE-B")).ProductCode);
    }

    [Fact]
    public void Default_reclaim_is_skipped_for_small_heaps_and_never_throws()
    {
        // 默认动作在堆小于门槛时直接返回；测试进程的堆远小于 128MB，这里只确认调用安全。
        var index = new LocalSellableItemIndex();

        var exception = Record.Exception(() => index.ReclaimMemory());

        Assert.Null(exception);
    }

    private static SellableItemDto CreateItem(string productCode)
    {
        return new SellableItemDto(
            "S001",
            productCode,
            null,
            $"Item {productCode}",
            $"CODE-{productCode}",
            null,
            $"CODE-{productCode}",
            1m,
            PriceSourceKind.ProductBase,
            "Product",
            1m,
            null);
    }
}
