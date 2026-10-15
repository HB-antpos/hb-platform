using System.Collections.Concurrent;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 「一码多商品」待选队列在工作流层的行为：弹窗期间继续扫码的商品照常加购、待选先进先出且互不覆盖、
/// 选定后的入车结果（数量 / 顺序）正确、各种边界不丢商品。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class PendingScanChoiceWorkflowTests
{
    private const string ConflictA = "930211";
    private const string ConflictB = "930212";

    [Fact]
    public void Scanning_continues_while_choices_are_pending_and_nothing_is_lost()
    {
        var fixture = CreateFixture();

        // 现场序列：多商品码 A → 普通商品 U1 → 多商品码 B → 普通商品 U2（旧行为下 A、B 会被后面的扫码顶掉）。
        var scanA = fixture.Scan(ConflictA);
        var scanU1 = fixture.Scan("930301");
        var scanB = fixture.Scan(ConflictB);
        var scanU2 = fixture.Scan("930302");

        Assert.Equal("pos.status.pendingChoiceQueued", scanA.StatusKey);
        Assert.Equal("pos.status.added", scanU1.StatusKey);
        Assert.Equal("pos.status.pendingChoiceQueued", scanB.StatusKey);
        Assert.Equal("pos.status.added", scanU2.StatusKey);
        // 唯一命中的扫码照常加购、顺序=扫码顺序；待选两项都还在，队首是最早的 A。
        Assert.Equal(["Plain 1", "Plain 2"], fixture.Cart.Lines.Select(line => line.DisplayName));
        var queue = fixture.Service.PendingChoices!;
        Assert.Equal(2, queue.Count);
        Assert.Equal(ConflictA, queue.Current!.Barcode);
        Assert.Equal([ConflictA, ConflictB], queue.Snapshot().Select(entry => entry.Barcode));

        // 收银员回来选：A 选 Small、B 选 Large。先进先出，选定的商品此刻入车、追加到末尾。
        var resolveA = fixture.Service.ResolvePendingChoice(Session, queue.Current!.Id, fixture.ASmall);
        Assert.Equal("pos.status.pendingChoiceResolved", resolveA.StatusKey);
        Assert.Equal(new object[] { "A Small", 1m, 1 }, resolveA.StatusArgs);
        Assert.Equal(ConflictB, queue.Current!.Barcode);

        var resolveB = fixture.Service.ResolvePendingChoice(Session, queue.Current!.Id, fixture.BLarge);
        Assert.Equal("pos.status.pendingChoiceResolvedLast", resolveB.StatusKey);
        Assert.Equal(new object[] { "B Large", 1m, 0 }, resolveB.StatusArgs);
        Assert.Equal(0, queue.Count);
        Assert.Equal(
            ["Plain 1", "Plain 2", "A Small", "B Large"],
            fixture.Cart.Lines.Select(line => line.DisplayName));
        Assert.All(fixture.Cart.Lines, line => Assert.Equal(1m, line.Quantity));
    }

    [Fact]
    public void Scanning_unique_items_never_touches_pending_choices()
    {
        var fixture = CreateFixture();
        fixture.Scan(ConflictA);
        var before = fixture.Service.PendingChoices!.Snapshot();

        var unique = fixture.Scan("930301");

        // 唯一命中的结果带 MatchesPopupOpen=false 只管手工搜索下拉；待选队列与它无关，原样保留。
        Assert.False(unique.MatchesPopupOpen);
        Assert.Equal(before, fixture.Service.PendingChoices!.Snapshot());
        Assert.Single(fixture.Cart.Lines);
    }

    [Fact]
    public void Resolved_item_merges_into_an_existing_line_with_the_correct_quantity()
    {
        var fixture = CreateFixture();
        fixture.Cart.AddItem(fixture.ASmall);
        fixture.Scan(ConflictA);
        fixture.Scan("930301");
        fixture.Scan(ConflictA);

        // 同一个多商品码连扫两次 = 两个待选；两次都选 A Small，沿用手工选择的合并规则：并进已有那一行，数量 3。
        var queue = fixture.Service.PendingChoices!;
        Assert.Equal(2, queue.Count);
        Assert.True(queue.Snapshot()[1].SameBarcodeAsPrevious);
        fixture.Service.ResolvePendingChoice(Session, queue.Current!.Id, fixture.ASmall);
        fixture.Service.ResolvePendingChoice(Session, queue.Current!.Id, fixture.ASmall);

        Assert.Equal(0, queue.Count);
        var smallLine = Assert.Single(fixture.Cart.Lines, line => line.DisplayName == "A Small");
        Assert.Equal(3m, smallLine.Quantity);
        Assert.Equal(["A Small", "Plain 1"], fixture.Cart.Lines.Select(line => line.DisplayName));
    }

    [Fact]
    public void Resolving_an_entry_that_no_longer_exists_does_not_add_anything()
    {
        var fixture = CreateFixture();
        fixture.Scan(ConflictA);
        var entry = fixture.Service.PendingChoices!.Current!;
        fixture.Service.ResolvePendingChoice(Session, entry.Id, fixture.ASmall);

        // 重复点击 / 界面上的旧点击：条目已经出队，第二次不能再加购一件。
        var again = fixture.Service.ResolvePendingChoice(Session, entry.Id, fixture.ASmall);

        Assert.Equal("pos.status.pendingChoiceGone", again.StatusKey);
        var line = Assert.Single(fixture.Cart.Lines);
        Assert.Equal(1m, line.Quantity);
    }

    [Fact]
    public void Resolving_with_a_product_that_is_not_a_candidate_keeps_the_entry()
    {
        var fixture = CreateFixture();
        fixture.Scan(ConflictA);
        var entry = fixture.Service.PendingChoices!.Current!;

        var result = fixture.Service.ResolvePendingChoice(Session, entry.Id, fixture.BLarge);

        Assert.Equal("pos.status.pendingChoiceGone", result.StatusKey);
        Assert.Empty(fixture.Cart.Lines);
        Assert.Equal(entry.Id, fixture.Service.PendingChoices!.Current!.Id);
    }

    [Fact]
    public void A_failed_add_keeps_the_entry_so_the_cashier_can_choose_again()
    {
        var index = new LocalSellableItemIndex();
        var weighed = CreateItem("P-W", "Weighed", ConflictA, quantityFactor: 1.5m);
        var normal = CreateItem("P-N", "Normal", ConflictA);
        index.ReplaceAll([weighed, normal]);
        var cart = new PosCartService();
        var service = new PosTerminalWorkflowService(index, cart);
        service.ProcessScan(Session, ConflictA, preferExactLookup: true, source: "raw");
        var entry = service.PendingChoices!.Current!;

        var failed = service.ResolvePendingChoice(Session, entry.Id, weighed);

        Assert.Equal("cart.status.quantityMustBeInteger", failed.StatusKey);
        Assert.Empty(cart.Lines);
        Assert.Equal(entry.Id, service.PendingChoices!.Current!.Id);

        var ok = service.ResolvePendingChoice(Session, entry.Id, normal);
        Assert.Equal("pos.status.pendingChoiceResolvedLast", ok.StatusKey);
        Assert.Equal("Normal", Assert.Single(cart.Lines).DisplayName);
        Assert.Equal(0, service.PendingChoices!.Count);
    }

    [Fact]
    public void A_full_queue_rejects_the_scan_loudly_without_dropping_pending_items()
    {
        var fixture = CreateFixture();
        for (var index = 0; index < PendingScanChoiceQueue.Capacity; index++)
        {
            fixture.Scan(ConflictA);
        }

        var rejected = fixture.Scan(ConflictB);

        Assert.Equal("pos.status.pendingChoiceFull", rejected.StatusKey);
        Assert.Equal(new object[] { PendingScanChoiceQueue.Capacity }, rejected.StatusArgs);
        Assert.True(rejected.ScanConflictHandled);
        Assert.Equal(PendingScanChoiceQueue.Capacity, fixture.Service.PendingChoices!.Count);
        Assert.All(fixture.Service.PendingChoices.Snapshot(), entry => Assert.Equal(ConflictA, entry.Barcode));
    }

    [Fact]
    public void Manually_entered_conflicting_code_keeps_the_dropdown_and_does_not_queue()
    {
        var fixture = CreateFixture();

        var manual = fixture.Service.ProcessScan(Session, ConflictA, preferExactLookup: false, source: "manual");

        // 手工输入回车时收银员正盯着下拉，沿用现有行为；只有扫码枪来源才进待选队列。
        Assert.Equal("pos.status.multipleMatches", manual.StatusKey);
        Assert.True(manual.MatchesPopupOpen);
        Assert.False(manual.ScanConflictHandled);
        Assert.Equal(0, fixture.Service.PendingChoices!.Count);
    }

    [Fact]
    public void Abandon_removes_one_or_all_and_reports_what_it_did()
    {
        var fixture = CreateFixture();
        fixture.Scan(ConflictA);
        fixture.Scan(ConflictB);
        fixture.Scan(ConflictA);
        var queue = fixture.Service.PendingChoices!;
        var first = queue.Snapshot()[0];

        Assert.True(fixture.Service.AbandonPendingChoice(first.Id, "cashier"));
        Assert.False(fixture.Service.AbandonPendingChoice(first.Id, "cashier"));
        Assert.Equal(2, queue.Count);

        Assert.Equal(2, fixture.Service.AbandonAllPendingChoices("pay"));
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, fixture.Service.AbandonAllPendingChoices("pay"));
        Assert.Empty(fixture.Cart.Lines);
    }

    [Fact]
    public void Advancing_the_cart_generation_does_not_discard_pending_choices()
    {
        var fixture = CreateFixture();
        fixture.Scan(ConflictA);

        // 世代只管在途补查；删掉购物车里最后一行（购物车变空）不该让还没选的商品悄悄消失。
        fixture.Service.AdvanceCartGeneration("cart-emptied");

        Assert.Equal(1, fixture.Service.PendingChoices!.Count);
    }

    [Fact]
    public async Task Remote_lookup_add_for_a_local_miss_leaves_pending_choices_alone()
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("P-S", "A Small", ConflictA), CreateItem("P-L", "A Large", ConflictA)]);
        var remote = CreateItem("P-R", "Remote Juice", "930999");
        var cart = new PosCartService();
        var service = new PosTerminalWorkflowService(
            index,
            cart,
            remoteLookupRefreshAsync: (storeCode, lookupCode, _) =>
                Task.FromResult(new RemoteLookupRefreshResult(storeCode, lookupCode, Found: true, remote, DeletedCount: 0)));
        service.ProcessScan(Session, ConflictA, preferExactLookup: true, source: "raw");

        var result = await service.ProcessScanAsync(Session, "930999", preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.False(result.MatchesPopupOpen);
        Assert.Equal("Remote Juice", Assert.Single(cart.Lines).DisplayName);
        Assert.Equal(1, service.PendingChoices!.Count);
    }

    [Fact]
    public void Queue_lifecycle_is_written_to_the_scan_log_with_enough_to_trace_it()
    {
        var logs = new ConcurrentQueue<string>();
        using var capture = CaptureClientLog(logs);
        var fixture = CreateFixture();

        fixture.Service.ProcessScan(Session, ConflictA, preferExactLookup: true, source: "raw", traceId: "raw-7");
        var entry = fixture.Service.PendingChoices!.Current!;
        fixture.Service.ResolvePendingChoice(Session, entry.Id, fixture.ASmall);
        fixture.Service.ProcessScan(Session, ConflictB, preferExactLookup: true, source: "keyboard-fallback", traceId: "keyboard-fallback-8");
        fixture.Service.AbandonAllPendingChoices("pay");

        var lines = logs.ToArray();
        // 入队：带 traceId、队列长度、候选商品；resolved 带等待时长与选了谁；abandoned 带原因。
        Assert.Contains(lines, line =>
            line.Contains("traceId=raw-7", StringComparison.Ordinal) &&
            line.Contains($"pending choice created id={entry.Id}", StringComparison.Ordinal) &&
            line.Contains("queueLength=1", StringComparison.Ordinal) &&
            line.Contains("candidates=2", StringComparison.Ordinal) &&
            line.Contains("candidateProducts=", StringComparison.Ordinal) &&
            line.Contains("P-A-S", StringComparison.Ordinal) &&
            line.Contains("P-A-L", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("traceId=raw-7", StringComparison.Ordinal) &&
            line.Contains($"pending choice resolved id={entry.Id}", StringComparison.Ordinal) &&
            line.Contains("productCode=P-A-S", StringComparison.Ordinal) &&
            line.Contains("waitedMs=", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("traceId=keyboard-fallback-8", StringComparison.Ordinal) &&
            line.Contains("pending choice abandoned", StringComparison.Ordinal) &&
            line.Contains("reason=pay", StringComparison.Ordinal));
        // 扫码主日志行保持原口径：待选不是「已加购」，autoAdded=false，运维统计才不会把它算成加购。
        Assert.Contains(lines, line =>
            line.Contains("traceId=raw-7", StringComparison.Ordinal) &&
            line.Contains("hit=search-multiple", StringComparison.Ordinal) &&
            line.Contains("autoAdded=false", StringComparison.Ordinal));
    }

    private static Fixture CreateFixture()
    {
        var index = new LocalSellableItemIndex();
        var aSmall = CreateItem("P-A-S", "A Small", ConflictA);
        var aLarge = CreateItem("P-A-L", "A Large", ConflictA);
        var bSmall = CreateItem("P-B-S", "B Small", ConflictB);
        var bLarge = CreateItem("P-B-L", "B Large", ConflictB);
        index.ReplaceAll(
        [
            aSmall,
            aLarge,
            bSmall,
            bLarge,
            CreateItem("P-1", "Plain 1", "930301"),
            CreateItem("P-2", "Plain 2", "930302")
        ]);
        var cart = new PosCartService();
        return new Fixture(new PosTerminalWorkflowService(index, cart), cart, aSmall, bLarge);
    }

    private static SellableItemDto CreateItem(string productCode, string name, string lookupCode, decimal quantityFactor = 1m)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: 2m,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "store",
            QuantityFactor: quantityFactor,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static IDisposable CaptureClientLog(ConcurrentQueue<string> lines)
    {
        void Capture(string line)
        {
            lines.Enqueue(line);
        }

        ConsoleLog.LineWritten += Capture;
        return new DisposableAction(() => ConsoleLog.LineWritten -= Capture);
    }

    private sealed record Fixture(
        PosTerminalWorkflowService Service,
        PosCartService Cart,
        SellableItemDto ASmall,
        SellableItemDto BLarge)
    {
        /// <summary>模拟一次扫码枪扫码（精确查找优先）。</summary>
        public PosTerminalWorkflowResult Scan(string barcode)
        {
            return Service.ProcessScan(Session, barcode, preferExactLookup: true, source: "raw");
        }
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}
