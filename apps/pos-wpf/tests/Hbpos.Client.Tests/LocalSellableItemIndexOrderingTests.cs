using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 去掉 ReplaceAll 的全局文化排序后，所有读取顺序必须与旧实现（<see cref="LegacySellableItemIndexReference"/>）等价：
/// GetFirst 前 N 个、Items、同码多商品的桶内顺序、Search 结果，以及 Upsert / RemoveLookup 之后的状态。
/// </summary>
public sealed class LocalSellableItemIndexOrderingTests
{
    // 名称刻意混合大小写、重音、中文与重复，覆盖"文化比较视为相等"的并列情形（并列时到达顺序决定先后）。
    private static readonly string[] NameWords =
    [
        "Apple", "apple", "APPLE", "Éclair", "eclair", "Café", "cafe", "Zebra", "mug", "MUG", "Straße", "苹果", "杯子", "Tea Pot", "tea pot",
    ];

    [Fact]
    public void Reads_match_legacy_implementation_after_ReplaceAll()
    {
        var items = CreateRandomItems(seed: 7, count: 3000, stores: ["S001", "S002"]);
        var (actual, expected) = CreatePair(items);

        AssertEquivalent(actual, expected, items);
    }

    [Fact]
    public void Reads_match_legacy_implementation_after_random_Upsert_and_RemoveLookup()
    {
        var items = CreateRandomItems(seed: 11, count: 1500, stores: ["S001", "S002"]);
        var (actual, expected) = CreatePair(items);
        var random = new Random(99);

        for (var step = 0; step < 400; step++)
        {
            var template = items[random.Next(items.Count)];
            if (random.Next(4) == 0)
            {
                actual.RemoveLookup(template.StoreCode, template.LookupCode);
                expected.RemoveLookup(template.StoreCode, template.LookupCode);
            }
            else
            {
                // 改名（可能与别的商品并列）、换价、偶尔换成全新的码。
                var upserted = template with
                {
                    DisplayName = NameWords[random.Next(NameWords.Length)] + " " + random.Next(30),
                    RetailPrice = random.Next(1, 100),
                    LookupCode = random.Next(5) == 0 ? "NEW" + random.Next(200) : template.LookupCode,
                };
                actual.Upsert(upserted);
                expected.Upsert(upserted);
            }

            if (step % 50 == 0)
            {
                AssertEquivalent(actual, expected, items);
            }
        }

        AssertEquivalent(actual, expected, items);
        Assert.Equal(expected.Count, actual.Count);
    }

    [Fact]
    public void GetFirst_keeps_arrival_order_for_names_that_compare_equal()
    {
        // 三个名称在忽略大小写后相等：稳定排序要求保持传入顺序，且夹在中间的别的名称不能打乱它们。
        var first = Item("P1", "apple 1", "C1");
        var second = Item("P2", "APPLE 1", "C2");
        var third = Item("P3", "Apple 1", "C3");
        var zebra = Item("P4", "zebra", "C4");
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([zebra, second, first, third]);

        Assert.Equal(["P2", "P1", "P3"], index.GetFirst("S001", 3).Select(item => item.ProductCode));
        Assert.Equal(["P2", "P1", "P3", "P4"], index.Items.Select(item => item.ProductCode));
    }

    [Fact]
    public void Same_code_bucket_is_ordered_by_name_with_arrival_order_for_ties()
    {
        var late = Item("P-LATE", "banana", "SAMECODE");
        var tieA = Item("P-A", "apple", "SAMECODE");
        var tieB = Item("P-B", "APPLE", "SAMECODE");
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([late, tieA, tieB]);

        Assert.Equal(["P-A", "P-B", "P-LATE"], index.FindExactMatches("S001", "samecode").Select(item => item.ProductCode));
    }

    [Fact]
    public void Upsert_and_RemoveLookup_are_visible_to_queries_without_global_order()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([Item("P1", "beta", "C1"), Item("P2", "gamma", "C2")]);

        index.Upsert(Item("P3", "alpha", "C3"));
        Assert.Equal(["P3", "P1", "P2"], index.GetFirst("S001", 8).Select(item => item.ProductCode));
        Assert.Equal("P3", Assert.Single(index.FindExactMatches("S001", "c3")).ProductCode);
        Assert.Equal("P3", Assert.Single(index.Search("S001", "alpha")).ProductCode);

        Assert.True(index.RemoveLookup("S001", "C1"));
        Assert.Empty(index.FindExactMatches("S001", "C1"));
        Assert.Equal(["P3", "P2"], index.GetFirst("S001", 8).Select(item => item.ProductCode));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public async Task FindExactMatches_never_sees_half_built_index_while_ReplaceAll_swaps()
    {
        // 整表替换期间并发扫码：每次查到的必须是旧快照或新快照里的完整桶，不能是空桶或半成品。
        const string StoreCode = "S001";
        var oldItems = Enumerable.Range(0, 20000).Select(i => Item("OLD" + i, "old " + i, "A" + i)).ToList();
        var newItems = Enumerable.Range(0, 20000).Select(i => Item("NEW" + i, "new " + i, "A" + i)).ToList();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(oldItems);

        using var stop = new CancellationTokenSource();
        var anomalies = new List<string>();
        var reader = Task.Run(() =>
        {
            var random = new Random(5);
            var lookups = 0;
            while (!stop.IsCancellationRequested)
            {
                var i = random.Next(20000);
                var matches = index.FindExactMatches(StoreCode, "A" + i);
                lookups++;
                if (matches.Count != 1 || (matches[0].ProductCode != "OLD" + i && matches[0].ProductCode != "NEW" + i))
                {
                    lock (anomalies)
                    {
                        anomalies.Add($"A{i}: {string.Join(",", matches.Select(item => item.ProductCode))}");
                    }
                }
            }

            return lookups;
        });

        for (var round = 0; round < 20; round++)
        {
            index.ReplaceAll(round % 2 == 0 ? newItems : oldItems);
        }

        stop.Cancel();
        var lookupCount = await reader;

        Assert.True(lookupCount > 0);
        Assert.Empty(anomalies);
    }

    [Fact]
    public async Task GetFirst_selection_does_not_hold_the_index_lock()
    {
        // 闸门验证：GetFirst 已越过锁、卡在选择开始处时，扫码的 FindExactMatches 必须能立即返回。
        // 若 GetFirst 仍在锁内扫描，FindExactMatches 会一直等到闸门放开，等待就会超时失败。
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([Item("P1", "beta", "C1"), Item("P2", "alpha", "C2")]);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        index.GetFirstSelectionStartedForTests = () =>
        {
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };

        var getFirst = Task.Run(() => index.GetFirst("S001", 8));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));

        var exact = Task.Run(() => index.FindExactMatches("S001", "C1"));
        var finished = await Task.WhenAny(exact, Task.Delay(TimeSpan.FromSeconds(10)));
        var exactReturnedWhileGetFirstBlocked = ReferenceEquals(finished, exact);
        release.Set();

        Assert.True(exactReturnedWhileGetFirstBlocked, "GetFirst 选择期间 FindExactMatches 被锁阻塞");
        Assert.Equal("P1", Assert.Single(await exact).ProductCode);
        Assert.Equal(["P2", "P1"], (await getFirst).Select(item => item.ProductCode));
    }

    private static (LocalSellableItemIndex Actual, LegacySellableItemIndexReference Expected) CreatePair(
        IReadOnlyList<SellableItemDto> items)
    {
        var actual = new LocalSellableItemIndex();
        var expected = new LegacySellableItemIndexReference();
        actual.ReplaceAll(items);
        expected.ReplaceAll(items);
        return (actual, expected);
    }

    private static void AssertEquivalent(
        LocalSellableItemIndex actual,
        LegacySellableItemIndexReference expected,
        IReadOnlyList<SellableItemDto> sampleItems)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(Names(expected.Items), Names(actual.Items));
        Assert.Equal(Names(expected.GetFirst(null, 8)), Names(actual.GetFirst(null, 8)));
        foreach (var store in new[] { "S001", "S002", "s001", "NONE" })
        {
            Assert.Equal(Names(expected.GetFirst(store, 8)), Names(actual.GetFirst(store, 8)));
            // 超过部分选择阈值时走另一条分支，也必须一致。
            Assert.Equal(Names(expected.GetFirst(store, 100)), Names(actual.GetFirst(store, 100)));
            Assert.Equal(Names(expected.GetFirst(store, 1)), Names(actual.GetFirst(store, 1)));
        }

        foreach (var item in sampleItems.Take(600))
        {
            Assert.Equal(
                Names(expected.FindExactMatches(item.StoreCode, item.LookupCode)),
                Names(actual.FindExactMatches(item.StoreCode, item.LookupCode)));
            Assert.Equal(
                Names(expected.FindMetadataExactMatches(item.StoreCode, item.ProductCode)),
                Names(actual.FindMetadataExactMatches(item.StoreCode, item.ProductCode)));
            Assert.Equal(
                Names(expected.FindMetadataExactMatches(item.StoreCode, item.Barcode!)),
                Names(actual.FindMetadataExactMatches(item.StoreCode, item.Barcode!)));
        }

        foreach (var query in new[] { "apple", "CAFÉ", "cafe", "mug 1", "苹果", "杯子", "tea", "dup", "NEW1", "1", "zzz" })
        {
            Assert.Equal(Names(expected.Search("S001", query, 50)), Names(actual.Search("S001", query, 50)));
            Assert.Equal(Names(expected.Search(null, query, 20)), Names(actual.Search(null, query, 20)));
        }
    }

    // 用"商品码|名称|价格"比较，能分辨同名但不同商品的先后顺序。
    private static string[] Names(IEnumerable<SellableItemDto> items) =>
        items.Select(item => $"{item.StoreCode}|{item.ProductCode}|{item.DisplayName}|{item.RetailPrice}").ToArray();

    private static List<SellableItemDto> CreateRandomItems(int seed, int count, string[] stores)
    {
        var random = new Random(seed);
        var items = new List<SellableItemDto>(count + count / 20);
        for (var i = 0; i < count; i++)
        {
            var name = NameWords[random.Next(NameWords.Length)] + " " + random.Next(30);
            items.Add(Item("P" + i, name, "C" + i, store: stores[random.Next(stores.Length)]));
        }

        // 约 5% 的码被第二个商品共用（码冲突候选），名称随机，可能与主商品并列。
        for (var i = 0; i < count / 20; i++)
        {
            var primary = items[random.Next(count)];
            items.Add(primary with
            {
                ProductCode = "PX" + i,
                DisplayName = NameWords[random.Next(NameWords.Length)] + " " + random.Next(30),
            });
        }

        return items;
    }

    private static SellableItemDto Item(string productCode, string name, string lookupCode, string store = "S001") =>
        new(
            StoreCode: store,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: 1m,
            PriceSource: PriceSourceKind.ProductBase,
            PriceSourceLabel: "base",
            QuantityFactor: 1m,
            UpdatedAt: null);
}
