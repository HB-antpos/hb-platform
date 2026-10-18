using System.Collections.Concurrent;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 扫码枪把「0 开头的 EAN-13」当 UPC-A 去掉前导 0（12 位）时，本地索引的回退规则：
/// 精确优先、无结果才回退前导 0 的另一种写法（仅纯数字 12↔13 位），其它长度 / 非数字一律不回退。
/// 真实案例：NMATS42BK 价签 0 810812024527，扫码枪读出 810812024527。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class BarcodeLeadingZeroVariantScanTests
{
    private const string Barcode13 = "0810812024527";
    private const string Barcode12 = "810812024527";

    [Fact]
    public void Scanner_12_digit_barcode_falls_back_to_the_13_digit_stored_barcode()
    {
        var (service, cart) = Create(CreateItem("NMATS42BK", "Nature's Melody Chime", Barcode13));
        var lines = new ConcurrentQueue<string>();
        using var logCapture = CaptureClientLog(lines);

        var result = service.ProcessScan(Session, Barcode12, preferExactLookup: true, source: "raw", traceId: "raw-1");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Equal("NMATS42BK", result.SelectedItem?.ProductCode);
        Assert.Equal(["Nature's Melody Chime"], cart.Lines.Select(line => line.DisplayName));
        // 命中口径要能在日志里和精确命中区分开，运维才能统计有多少扫码靠变体回退命中。
        Assert.Contains(lines, line =>
            line.Contains("traceId=raw-1", StringComparison.Ordinal) &&
            line.Contains("hit=lookup-variant", StringComparison.Ordinal) &&
            line.Contains("matchCount=1", StringComparison.Ordinal) &&
            line.Contains("autoAdded=true", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_13_digit_leading_zero_barcode_falls_back_to_the_12_digit_stored_barcode()
    {
        var (service, cart) = Create(CreateItem("UPC-A-ITEM", "Stored As UPC-A", Barcode12));

        var result = service.ProcessScan(Session, Barcode13, preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Equal(["Stored As UPC-A"], cart.Lines.Select(line => line.DisplayName));
    }

    [Fact]
    public void Exact_match_wins_when_the_12_and_13_digit_barcodes_belong_to_different_products()
    {
        // 生产库里 12 位 X 与 13 位 0+X 分属不同商品的情况真实存在（33 对）：各自扫各自的，绝不能互相串。
        var (service, cart) = Create(
            CreateItem("P-12", "Twelve Digit Product", Barcode12),
            CreateItem("P-13", "Thirteen Digit Product", Barcode13));
        var lines = new ConcurrentQueue<string>();
        using var logCapture = CaptureClientLog(lines);

        var scan12 = service.ProcessScan(Session, Barcode12, preferExactLookup: true, source: "raw", traceId: "raw-12");
        var scan13 = service.ProcessScan(Session, Barcode13, preferExactLookup: true, source: "raw", traceId: "raw-13");

        Assert.Equal("P-12", scan12.SelectedItem?.ProductCode);
        Assert.Equal("P-13", scan13.SelectedItem?.ProductCode);
        Assert.Equal(["Twelve Digit Product", "Thirteen Digit Product"], cart.Lines.Select(line => line.DisplayName));
        // 两次都是精确命中，没有走变体回退。
        Assert.Contains(lines, line => line.Contains("traceId=raw-12", StringComparison.Ordinal) && line.Contains("hit=lookup-exact", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("traceId=raw-13", StringComparison.Ordinal) && line.Contains("hit=lookup-exact", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("hit=lookup-variant", StringComparison.Ordinal));
    }

    [Theory]
    // 11 位不补 0（即使库里有 12 位的 0+X）。
    [InlineData("081081202452", "81081202452")]
    // 14 位不去 0（即使库里有 13 位的去掉一个 0 的写法）。
    [InlineData("0810812024527", "00810812024527")]
    // 13 位但不以 0 开头：不能去掉首位去撞 12 位商品。
    [InlineData("810812024527", "1810812024527")]
    // 含非数字：12 位补 0、13 位去 0 都不参与变体。
    [InlineData("0A10812024527", "A10812024527")]
    [InlineData("A10812024527", "0A10812024527")]
    public void Other_lengths_and_non_digit_barcodes_do_not_fall_back(string storedBarcode, string scannedBarcode)
    {
        var (service, cart) = Create(CreateItem("P-STORED", "Stored Product", storedBarcode));
        var lines = new ConcurrentQueue<string>();
        using var logCapture = CaptureClientLog(lines);

        var result = service.ProcessScan(Session, scannedBarcode, preferExactLookup: true, source: "raw", traceId: "raw-miss");

        Assert.Equal("pos.status.noLocalMatch", result.StatusKey);
        Assert.Empty(cart.Lines);
        Assert.Contains(lines, line =>
            line.Contains("traceId=raw-miss", StringComparison.Ordinal) &&
            line.Contains("hit=exact-not-found", StringComparison.Ordinal));
    }

    [Fact]
    public void Variant_matching_several_products_is_queued_for_the_cashier_instead_of_auto_added()
    {
        // 变体命中与精确命中同口径：唯一命中才自动加购，多个商品命中交给待选队列。
        var (service, cart) = Create(
            CreateItem("P-A", "Variant Product A", Barcode13),
            CreateItem("P-B", "Variant Product B", Barcode13));
        var lines = new ConcurrentQueue<string>();
        using var logCapture = CaptureClientLog(lines);

        var result = service.ProcessScan(Session, Barcode12, preferExactLookup: true, source: "raw", traceId: "raw-multi");

        Assert.Equal("pos.status.pendingChoiceQueued", result.StatusKey);
        Assert.Empty(cart.Lines);
        Assert.Equal(1, service.PendingChoices!.Count);
        Assert.Contains(lines, line =>
            line.Contains("traceId=raw-multi", StringComparison.Ordinal) &&
            line.Contains("hit=lookup-variant-multiple", StringComparison.Ordinal) &&
            line.Contains("matchCount=2", StringComparison.Ordinal) &&
            line.Contains("autoAdded=false", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_text_with_surrounding_whitespace_still_falls_back()
    {
        // 索引查找本来就会 Trim；变体判定与之对齐，不能因为多了空白就被当成「含非数字」。
        var (service, cart) = Create(CreateItem("NMATS42BK", "Nature's Melody Chime", Barcode13));

        var result = service.ProcessScan(Session, $" {Barcode12} ", preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Variant_hit_never_sends_the_scanned_code_to_the_remote_lookup()
    {
        var remoteCalls = new ConcurrentQueue<string>();
        var lines = new ConcurrentQueue<string>();
        using var logCapture = CaptureClientLog(lines);
        var (service, cart) = Create(
            (storeCode, lookupCode, _) =>
            {
                remoteCalls.Enqueue(lookupCode);
                return Task.FromResult(new RemoteLookupRefreshResult(storeCode, lookupCode, Found: false, Item: null, DeletedCount: 0));
            },
            CreateItem("NMATS42BK", "Nature's Melody Chime", Barcode13));

        var result = await service.ProcessScanAsync(Session, Barcode12, preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Single(cart.Lines);
        // 本地已经（经变体）命中，不走「本地未命中 → 远程补查」：扫到的 12 位码不会被拿去远程查，
        // 否则 POS API 精确匹配查不到，还会让客户端把本地条目当失效条目删掉。
        Assert.DoesNotContain(lines, line => line.Contains("after local miss", StringComparison.Ordinal));
        // 加购后的后台价格刷新是既有行为，用的是商品自己的 LookupCode（13 位），与扫到的码无关。
        Assert.All(remoteCalls, code => Assert.Equal(Barcode13, code));
    }

    [Fact]
    public async Task Variant_miss_still_falls_through_to_remote_lookup_with_the_scanned_code()
    {
        var remoteCalls = new ConcurrentQueue<string>();
        var (service, cart) = Create(
            (storeCode, lookupCode, _) =>
            {
                remoteCalls.Enqueue(lookupCode);
                return Task.FromResult(new RemoteLookupRefreshResult(storeCode, lookupCode, Found: false, Item: null, DeletedCount: 0));
            });

        var result = await service.ProcessScanAsync(Session, Barcode12, preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.noLocalMatch", result.StatusKey);
        Assert.Empty(cart.Lines);
        // 远程补查口径不变：仍用扫到的原始码（POS API 目前只做精确匹配）。
        Assert.Equal([Barcode12], remoteCalls.ToArray());
    }

    [Fact]
    public async Task Manual_input_of_the_12_digit_code_already_finds_the_13_digit_barcode_by_substring_search()
    {
        // 手工输入走关键词检索（LookupCode 子串匹配），12 位本来就能命中 13 位库内条码，不需要变体回退。
        var (service, cart) = Create(CreateItem("NMATS42BK", "Nature's Melody Chime", Barcode13));

        var result = await service.ProcessScanAsync(Session, Barcode12, preferExactLookup: false, source: "manual");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.Equal(["Nature's Melody Chime"], cart.Lines.Select(line => line.DisplayName));
    }

    [Theory]
    [InlineData("810812024527", "0810812024527")]
    [InlineData("0810812024527", "810812024527")]
    [InlineData("000000000000", "0000000000000")]
    [InlineData("0000000000000", "000000000000")]
    [InlineData("1810812024527", null)]
    [InlineData("81081202452", null)]
    [InlineData("00810812024527", null)]
    [InlineData("81081202452X", null)]
    [InlineData("0A10812024527", null)]
    // 全角数字不是 ASCII 数字，不参与变体。
    [InlineData("８１０８１２０２４５２７", null)]
    [InlineData(" 810812024527", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GetLeadingZeroVariant_only_swaps_pure_digit_12_and_13_digit_codes(string? barcode, string? expected)
    {
        Assert.Equal(expected, BarcodeVariants.GetLeadingZeroVariant(barcode));
    }

    private static (PosTerminalWorkflowService Service, PosCartService Cart) Create(params SellableItemDto[] items)
    {
        return Create(remoteLookupRefreshAsync: null, items);
    }

    private static (PosTerminalWorkflowService Service, PosCartService Cart) Create(
        Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? remoteLookupRefreshAsync,
        params SellableItemDto[] items)
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(items);
        var cart = new PosCartService();
        return (new PosTerminalWorkflowService(index, cart, remoteLookupRefreshAsync), cart);
    }

    private static SellableItemDto CreateItem(string productCode, string name, string lookupCode)
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
            QuantityFactor: 1m,
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

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}
