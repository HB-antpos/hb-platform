using System.Diagnostics;
using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public sealed record PosTerminalWorkflowResult
{
    public string? StatusKey { get; init; }

    public object[] StatusArgs { get; init; } = [];

    public SellableItemDto? SelectedItem { get; init; }

    public CartLine? SelectedCartLine { get; init; }

    public IReadOnlyList<SellableItemDto>? Matches { get; init; }

    public bool? MatchesPopupOpen { get; init; }

    public bool? TouchKeyboardOpen { get; init; }

    public bool? WholeOrderOperation { get; init; }

    public bool ClearScanText { get; init; }

    public bool ClearKeypadBuffer { get; init; }

    public bool PaymentAllowed { get; init; }

    /// <summary>
    /// 扫码枪扫到「一码多商品」并已记入待选队列（或队列已满被拒收）：搜索框里的条码已经处理完，界面要清空它。
    /// 不复用 <see cref="ClearScanText"/>，因为后者在扫码日志里表示「已自动加购」，会让运维统计把待选误算成加购。
    /// </summary>
    public bool ScanConflictHandled { get; init; }
}

public sealed class PosTerminalCatalogReloadedEventArgs : EventArgs
{
    public PosTerminalCatalogReloadedEventArgs(IReadOnlyList<SellableItemDto>? catalogItems = null)
    {
        HasCatalogSnapshot = catalogItems is not null;
        CatalogItems = catalogItems ?? [];
    }

    // 增量远程补录只通知索引已变更，避免为 UI 事件复制完整商品目录。
    public bool HasCatalogSnapshot { get; }

    public IReadOnlyList<SellableItemDto> CatalogItems { get; }
}

/// <summary>
/// 一次扫码附带的 UI 侧回调。工作流本身不依赖 UI，只在补查的关键节点同步通知调用方，
/// 由收银页决定如何展示（状态栏、提示音）以及如何记审计。回调抛出的异常会被工作流吞掉并记日志，不会打断扫码。
/// </summary>
public sealed record PosScanHooks
{
    /// <summary>
    /// 本地未命中、远程补查已发起（或加入了他人已发起的补查）时，在调用线程、首个 await 之前同步触发，
    /// 参数是中间状态结果（「正在查询商品…」）。补查要等最长 2 秒，这里让收银员在扫码的同一时刻就得到反馈。
    /// </summary>
    public Action<PosTerminalWorkflowResult>? OnInterimResult { get; init; }

    /// <summary>
    /// 补查结果已通过「购物车世代」校验、即将加购时同步触发，早于真正的加购。
    /// 补查结果回来时购物车可能已被其它扫码改动过，审计的「改前快照」应当在这一刻取，而不是扫码发起时。
    /// </summary>
    public Action? OnBeforeRemoteApply { get; init; }
}

public interface IPosTerminalWorkflowService
{
    event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

    PosTerminalWorkflowResult ProcessScan(PosSessionState session, string scanText, bool preferExactLookup, string source, string? traceId = null);

    Task<PosTerminalWorkflowResult> ProcessScanAsync(
        PosSessionState session,
        string scanText,
        bool preferExactLookup,
        string source,
        string? traceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 带 UI 回调的扫码入口。默认实现忽略回调并转调旧入口，
    /// 这样只实现了旧签名的替身 / 测试桩无需改动即可继续使用。
    /// </summary>
    Task<PosTerminalWorkflowResult> ProcessScanAsync(
        PosSessionState session,
        string scanText,
        bool preferExactLookup,
        string source,
        string? traceId,
        PosScanHooks? hooks,
        CancellationToken cancellationToken = default)
    {
        return ProcessScanAsync(session, scanText, preferExactLookup, source, traceId, cancellationToken);
    }

    /// <summary>
    /// 推进「购物车世代」：在此之前发起、尚未回来的远程补查结果一律作废（不加购，只提示）。
    /// 清空、开始收款、挂单 / 取单 / 退货、购物车被清空、收银会话变化、页面销毁时调用。默认实现为空。
    /// </summary>
    void AdvanceCartGeneration(string reason)
    {
    }

    /// <summary>
    /// 扫码枪扫到「一码多商品」时入队等待收银员选择的待选队列；与购物车世代无关（世代只管在途补查），
    /// 只有选定 / 放弃 / 清空购物车 / 会话切换 / 页面销毁才会让条目离开队列。默认实现为 null（不启用待选），
    /// 这样只实现了旧签名的替身 / 测试桩无需改动即可继续使用。
    /// </summary>
    PendingScanChoiceQueue? PendingChoices => null;

    /// <summary>把收银员选定的商品加入购物车并让该待选出队；加购失败时条目保留。默认实现什么也不做。</summary>
    PosTerminalWorkflowResult ResolvePendingChoice(PosSessionState session, long choiceId, SellableItemDto item)
    {
        return new PosTerminalWorkflowResult();
    }

    /// <summary>放弃一个待选（不加入购物车）。条目已不存在返回 false。默认实现什么也不做。</summary>
    bool AbandonPendingChoice(long choiceId, string reason)
    {
        return false;
    }

    /// <summary>放弃全部待选，返回被放弃的个数。默认实现什么也不做。</summary>
    int AbandonAllPendingChoices(string reason)
    {
        return 0;
    }

    PosTerminalWorkflowResult AddSelectedItem(PosSessionState session, SellableItemDto item, bool clearScanText, bool closeMatchesPopup, string operation);

    PosTerminalWorkflowResult AddOpenItem(PosSessionState session, string keypadBuffer);

    PosTerminalWorkflowResult RemoveLine(CartLine? line);

    PosTerminalWorkflowResult IncreaseLine(CartLine? line);

    PosTerminalWorkflowResult DecreaseLine(CartLine? line);

    PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer);

    PosTerminalWorkflowResult ModifySelectedLinePrice(CartLine? selectedLine, string keypadBuffer);

    PosTerminalWorkflowResult ApplySelectedLineDiscountAmount(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation);

    PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation);

    PosTerminalWorkflowResult ApplyQuickDiscountPercent(CartLine? selectedLine, string? value, bool isWholeOrderOperation);

    /// <summary>
    /// 折扣类操作（行折扣金额 / 百分比 / 快速折扣）的退货行门禁：行模式下选中退货行，返回应展示的拒绝提示；可以继续则返回 null。
    /// 整单折扣只作用于销售行、跳过退货行，与选中哪一行无关，整单模式放行；
    /// 只有购物车里仅有退货行、没有可打折的销售行时，整单模式给出「没有可打折的销售商品」的提示。
    /// 收银页在弹授权 / 主管 PIN 之前先问一次，避免为一个注定被拒绝的操作索要授权。
    /// 默认实现一律放行，这样只实现了旧签名的替身 / 测试桩无需改动即可继续使用。
    /// </summary>
    PosTerminalWorkflowResult? GetDiscountRejection(CartLine? selectedLine, bool isWholeOrderOperation)
    {
        return null;
    }

    /// <summary>改数量的退货行门禁，口径同 <see cref="GetDiscountRejection"/>：退货行的数量由退货流程锁定。</summary>
    PosTerminalWorkflowResult? GetQuantityEditRejection(CartLine? selectedLine)
    {
        return null;
    }

    /// <summary>改价的退货行门禁，口径同 <see cref="GetDiscountRejection"/>：退货行的单价由退货流程锁定。</summary>
    PosTerminalWorkflowResult? GetPriceEditRejection(CartLine? selectedLine)
    {
        return null;
    }

    PosTerminalWorkflowResult ClearCart();

    PosTerminalWorkflowResult GuardPayment();
}

public sealed class PosTerminalWorkflowService : IPosTerminalWorkflowService
{
    // internal：测试通过 InternalsVisibleTo 引用该值推进虚拟时间，避免在测试里复制一份 2 秒常量。
    internal static readonly TimeSpan RemoteLookupTimeout = TimeSpan.FromSeconds(2);
    private const string OpenItemLookupCode = "OPENITEM";

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? _remoteLookupRefreshAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _reloadCatalogAsync;
    private readonly IUiPriorityCoordinator _uiPriorityCoordinator;
    private readonly Func<bool> _isCatalogSyncActive;
    // 远程查询超时计时器走 TimeProvider，测试可注入 FakeTimeProvider 直接推进到超时点。
    private readonly TimeProvider _timeProvider;
    private readonly object _remoteLookupGate = new();
    private readonly HashSet<RemoteLookupKey> _pendingRemoteLookups = [];
    private readonly Dictionary<RemoteLookupKey, Task<RemoteLookupRefreshResult>> _pendingRemoteLookupTasks = [];
    // 本地未命中后「正在补查并等待加购」的扫码，按（商品码, 购物车世代）登记，受 _remoteLookupGate 保护。
    // 同码同世代只有第一次扫码等待结果并加购，后续重复扫码直接提示「正在查询」。
    private readonly HashSet<PendingRemoteScan> _pendingRemoteScans = [];
    // 购物车世代：每发生一次「这一单的购物车已经不是扫码时那一单」的事件就加一。
    // 补查发起时记下当时的世代，结果回来时比对，不一致就丢弃，不依赖时间判断。
    private long _cartGeneration;
    // 扫码枪「一码多商品」待选队列：与购物车世代无关，生命周期跟随本工作流实例（页面重建即丢弃，销毁时会记日志）。
    private readonly PendingScanChoiceQueue _pendingChoices;

    public PosTerminalWorkflowService(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? remoteLookupRefreshAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? reloadCatalogAsync = null,
        IUiPriorityCoordinator? uiPriorityCoordinator = null,
        Func<bool>? isCatalogSyncActive = null,
        TimeProvider? timeProvider = null)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _remoteLookupRefreshAsync = remoteLookupRefreshAsync;
        _reloadCatalogAsync = reloadCatalogAsync;
        _uiPriorityCoordinator = uiPriorityCoordinator ?? UiPriorityCoordinator.Noop;
        _isCatalogSyncActive = isCatalogSyncActive ?? (() => false);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pendingChoices = new PendingScanChoiceQueue(_timeProvider);
    }

    public event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

    public PendingScanChoiceQueue? PendingChoices => _pendingChoices;

    public PosTerminalWorkflowResult ProcessScan(PosSessionState session, string scanText, bool preferExactLookup, string source, string? traceId = null)
    {
        using var uiOperation = _uiPriorityCoordinator.BeginUiOperation("process-scan");
        var submittedScanText = scanText;
        var totalStopwatch = Stopwatch.StartNew();
        var exactLookupElapsedMs = 0L;
        var searchElapsedMs = 0L;
        var matchKind = preferExactLookup ? "exact-not-found" : "search";
        var matchCount = 0;
        var autoAdded = false;
        PosTerminalWorkflowResult result;

        if (preferExactLookup)
        {
            var exactLookupStopwatch = Stopwatch.StartNew();
            var exactMatches = _priceIndex.FindExactMatches(session.StoreCode, submittedScanText);
            exactLookupStopwatch.Stop();
            exactLookupElapsedMs = exactLookupStopwatch.ElapsedMilliseconds;
            matchKind = exactMatches.Count switch
            {
                0 => "exact-not-found",
                1 => "lookup-exact",
                _ => "search-multiple"
            };

            var matches = exactMatches;
            var allowAutoAdd = exactMatches.Count == 1;
            if (exactMatches.Count == 0)
            {
                // 关键逻辑：扫码枪常把「0 开头的 EAN-13」当 UPC-A 去掉前导 0（12 位），而库里按价签的 13 位存放（反之亦然）。
                // 必须「精确优先、无结果才回退」：生产库里 12 位 X 与 13 位 0+X 分属不同商品的情况真实存在，
                // 所以只有原样精确查不到时才试前导 0 的另一种写法，绝不把两种写法合并去查。
                var variantMatches = FindLeadingZeroVariantMatches(session.StoreCode, submittedScanText);
                if (variantMatches.Count > 0)
                {
                    matches = variantMatches;
                    // 与精确命中同口径：唯一命中才自动加购，多个商品命中走待选队列让收银员选。
                    allowAutoAdd = variantMatches.Count == 1;
                    matchKind = variantMatches.Count > 1 ? "lookup-variant-multiple" : "lookup-variant";
                }
                else
                {
                    var metadataMatches = _priceIndex.FindMetadataExactMatches(session.StoreCode, submittedScanText);
                    if (metadataMatches.Count > 0)
                    {
                        matches = metadataMatches;
                        matchKind = metadataMatches.Count > 1 ? "metadata-duplicate" : "metadata-only";
                    }
                }
            }

            // preferExactLookup 即扫码枪入口：多商品命中不再弹下拉覆盖上一个，而是记入待选队列（手工输入走下面的 else 分支，沿用下拉）。
            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd, traceId, source, queueConflicts: true);
            matchCount = matches.Count;
            autoAdded = result.ClearScanText;
        }
        else
        {
            var exactLookupStopwatch = Stopwatch.StartNew();
            var exactMatches = _priceIndex.FindExactMatches(session.StoreCode, submittedScanText);
            exactLookupStopwatch.Stop();
            exactLookupElapsedMs = exactLookupStopwatch.ElapsedMilliseconds;
            var hasDuplicateExactMatch = exactMatches.Count > 1;
            if (hasDuplicateExactMatch)
            {
                matchKind = "search-multiple";
            }

            // 码冲突时只列精确命中的商品，口径同 ProcessSearchScanAsync。
            var searchStopwatch = Stopwatch.StartNew();
            var matches = hasDuplicateExactMatch
                ? exactMatches
                : _priceIndex.Search(session.StoreCode, submittedScanText);
            searchStopwatch.Stop();
            searchElapsedMs = searchStopwatch.ElapsedMilliseconds;

            // 手工输入的码精确命中多个商品（码冲突）同样记入待选队列，不选不消失；关键词模糊搜索（非精确冲突）仍用下拉，那是在浏览。
            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd: !hasDuplicateExactMatch, traceId, source, queueConflicts: hasDuplicateExactMatch);
            matchCount = matches.Count;
            autoAdded = result.ClearScanText;
        }

        totalStopwatch.Stop();
        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(traceId)}barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(submittedScanText)} storeCode={session.StoreCode} source={source} hit={matchKind} matchCount={matchCount} autoAdded={FormatBool(autoAdded)} cartLines={_cart.Lines.Count} exactLookupElapsedMs={exactLookupElapsedMs} searchElapsedMs={searchElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");

        return result;
    }

    /// <summary>
    /// 用前导 0 的另一种写法再精确查一次本地索引；只在原样精确查找无结果后由调用方调用。
    /// 非「纯数字 12/13 位」的条码没有变体，直接返回空。
    /// </summary>
    private IReadOnlyList<SellableItemDto> FindLeadingZeroVariantMatches(string storeCode, string scanText)
    {
        // 索引查找会先 Trim，这里与之对齐，避免带空白的扫码文本因为不是纯数字而错过变体。
        var variant = BarcodeVariants.GetLeadingZeroVariant(scanText.Trim());
        return variant is null
            ? []
            : _priceIndex.FindExactMatches(storeCode, variant);
    }

    public Task<PosTerminalWorkflowResult> ProcessScanAsync(
        PosSessionState session,
        string scanText,
        bool preferExactLookup,
        string source,
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return ProcessScanAsync(session, scanText, preferExactLookup, source, traceId, hooks: null, cancellationToken);
    }

    public async Task<PosTerminalWorkflowResult> ProcessScanAsync(
        PosSessionState session,
        string scanText,
        bool preferExactLookup,
        string source,
        string? traceId,
        PosScanHooks? hooks,
        CancellationToken cancellationToken = default)
    {
        // 关键逻辑：扫码发起的这一刻就记下购物车世代；本地未命中走远程补查时，结果回来再与当前世代比对。
        var cartGeneration = Volatile.Read(ref _cartGeneration);
        var localResult = preferExactLookup
            ? ProcessScan(session, scanText, preferExactLookup, source, traceId)
            : await ProcessSearchScanAsync(session, scanText, source, traceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLocalMiss(localResult))
        {
            return localResult;
        }

        return await TryAddRemoteLookupMissAsync(session, scanText, source, traceId, localResult, cartGeneration, hooks, cancellationToken);
    }

    public void AdvanceCartGeneration(string reason)
    {
        var generation = Interlocked.Increment(ref _cartGeneration);
        int pendingScanCount;
        lock (_remoteLookupGate)
        {
            pendingScanCount = _pendingRemoteScans.Count;
        }

        // 购物车每次变空都会走到这里；只有确实有在途补查被作废时才写日志，避免刷屏。
        if (pendingScanCount > 0)
        {
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup pending scans invalidated reason={reason} cartGeneration={generation} pendingScans={pendingScanCount}");
        }
    }

    private async Task<PosTerminalWorkflowResult> ProcessSearchScanAsync(
        PosSessionState session,
        string scanText,
        string source,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var submittedScanText = scanText;
        var exactLookupStopwatch = Stopwatch.StartNew();
        // 手工检索的精确命中可能复制大量重复条目，先离开 Dispatcher 再继续匹配流程。
        var exactMatches = await _priceIndex.FindExactMatchesAsync(
            session.StoreCode,
            submittedScanText,
            cancellationToken);
        exactLookupStopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var hasDuplicateExactMatch = exactMatches.Count > 1;
        var matchKind = hasDuplicateExactMatch ? "search-multiple" : "search";

        // 输入的码精确命中多个商品（码冲突）时只列这些商品，与扫码枪口径一致：关键词搜索会把同一商品
        // 按它自己的货号再列一次，且只取前 20 条，候选多时排在后面的商品选不到。
        var searchStopwatch = Stopwatch.StartNew();
        var matches = hasDuplicateExactMatch
            ? exactMatches
            : await _priceIndex.SearchAsync(session.StoreCode, submittedScanText, cancellationToken);
        searchStopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        PosTerminalWorkflowResult result;
        using (_uiPriorityCoordinator.BeginUiOperation("process-scan"))
        {
            // 手工输入的码精确命中多个商品（码冲突）同样记入待选队列，不选不消失；关键词模糊搜索（非精确冲突）仍用下拉，那是在浏览。
            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd: !hasDuplicateExactMatch, traceId, source, queueConflicts: hasDuplicateExactMatch);
        }

        totalStopwatch.Stop();
        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(traceId)}barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(submittedScanText)} storeCode={session.StoreCode} source={source} hit={matchKind} matchCount={matches.Count} autoAdded={FormatBool(result.ClearScanText)} cartLines={_cart.Lines.Count} exactLookupElapsedMs={exactLookupStopwatch.ElapsedMilliseconds} searchElapsedMs={searchStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return result;
    }

    public PosTerminalWorkflowResult AddSelectedItem(
        PosSessionState session,
        SellableItemDto item,
        bool clearScanText,
        bool closeMatchesPopup,
        string operation)
    {
        using var uiOperation = _uiPriorityCoordinator.BeginUiOperation(operation);
        return AddItem(session, item, clearScanText, closeMatchesPopup, operation);
    }

    public PosTerminalWorkflowResult AddOpenItem(PosSessionState session, string keypadBuffer)
    {
        using var uiOperation = _uiPriorityCoordinator.BeginUiOperation("manual-add-open-item");
        if (!TryGetKeypadValue(keypadBuffer, out var unitPrice) || unitPrice <= 0m)
        {
            return Status("pos.status.invalidKeypadValue");
        }

        var matches = _priceIndex.FindExactMatches(session.StoreCode, OpenItemLookupCode);
        if (matches.Count == 0)
        {
            return Status("pos.status.noLocalMatch") with
            {
                Matches = matches,
                MatchesPopupOpen = false
            };
        }

        if (matches.Count > 1)
        {
            return Status("pos.status.multipleMatches", matches.Count) with
            {
                Matches = matches,
                SelectedItem = matches[0],
                MatchesPopupOpen = true
            };
        }

        var item = matches[0];
        CartLine line;
        try
        {
            line = _cart.AddOpenItem(item, unitPrice);
        }
        catch (InvalidOperationException)
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        ConsoleLog.Write(
            "CartPerf",
            $"operation=manual-add-open-item storeCode={session.StoreCode} productCode={LogValue(item.ProductCode)} lookupCode={LogValue(item.LookupCode)} unitPrice={FormatAmount(unitPrice)} cartLines={_cart.Lines.Count}");
        return Status("pos.status.added", item.DisplayName) with
        {
            SelectedItem = item,
            SelectedCartLine = line,
            ClearKeypadBuffer = true,
            MatchesPopupOpen = false,
            TouchKeyboardOpen = false
        };
    }

    public PosTerminalWorkflowResult RemoveLine(CartLine? line)
    {
        if (line is null || !_cart.RemoveLine(line))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.ready");
    }

    public PosTerminalWorkflowResult IncreaseLine(CartLine? line)
    {
        if (line is null)
        {
            return new PosTerminalWorkflowResult();
        }

        return !_cart.IncreaseLine(line)
            ? Status("cart.status.quantityMustBeInteger")
            : Status("pos.status.ready") with { SelectedCartLine = line };
    }

    public PosTerminalWorkflowResult DecreaseLine(CartLine? line)
    {
        if (line is null)
        {
            return new PosTerminalWorkflowResult();
        }

        if (!_cart.DecreaseLine(line))
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        return _cart.Lines.Contains(line)
            ? Status("pos.status.ready") with { SelectedCartLine = line }
            : Status("pos.status.ready");
    }

    public PosTerminalWorkflowResult? GetDiscountRejection(CartLine? selectedLine, bool isWholeOrderOperation)
    {
        // 整单折扣只作用于销售行、跳过退货行（含退货行的购物车也能设置），而且作用于整张购物车、不看选中哪一行：
        // 即使当前选中的恰好是退货行，整单模式也必须放行，不能被下面的「退货商品不能修改折扣」误拦。
        // 唯一的例外：购物车里只有退货行、没有可打折的销售行，整单折扣无处可落，直接提示，
        // 收银页据此在弹授权 / 主管 PIN 之前就拦下。空购物车、无退货行的购物车不受影响，仍走各自既有的提示。
        if (isWholeOrderOperation)
        {
            return _cart.HasNoOrderDiscountableSaleLine ? Status("pos.status.orderDiscountNoSaleLines") : null;
        }

        return selectedLine is { IsReturnLine: true } ? Status("pos.status.returnLineDiscountBlocked") : null;
    }

    public PosTerminalWorkflowResult? GetQuantityEditRejection(CartLine? selectedLine)
    {
        return selectedLine is { IsReturnLine: true } ? Status("pos.status.returnLineQuantityBlocked") : null;
    }

    public PosTerminalWorkflowResult? GetPriceEditRejection(CartLine? selectedLine)
    {
        return selectedLine is { IsReturnLine: true } ? Status("pos.status.returnLinePriceBlocked") : null;
    }

    public PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer)
    {
        // 退货行的数量由退货流程锁定，购物车一律拒绝；在这里先给出明确提示，不再让它落成静默的空结果。
        // 放在键盘输入校验之前：输入值无论对错，这一行都改不了，提示「输入有效数值」只会误导。
        if (GetQuantityEditRejection(selectedLine) is { } quantityRejection)
        {
            return quantityRejection;
        }

        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        if (value <= 0m)
        {
            return Status("pos.status.quantityMustBePositive");
        }

        if (!PosCartService.IsPositiveIntegerQuantity(value))
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        if (!_cart.SetLineQuantity(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.lineQuantityUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    public PosTerminalWorkflowResult ModifySelectedLinePrice(CartLine? selectedLine, string keypadBuffer)
    {
        // 同改数量：退货行的单价由退货流程锁定，先给明确提示。
        if (GetPriceEditRejection(selectedLine) is { } priceRejection)
        {
            return priceRejection;
        }

        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        if (!_cart.SetLineUnitPrice(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.linePriceUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    public PosTerminalWorkflowResult ApplySelectedLineDiscountAmount(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
    {
        // 行模式下选中退货行一律拒绝并给出明确提示：
        // 退货行毛额为负，不拦的话会拿负数去比折扣金额，要么误报「折扣金额过高」，要么落到购物车的静默拒绝。
        // 整单模式不看选中行，由购物车只对销售行分摊（见 GetDiscountRejection）。
        if (GetDiscountRejection(selectedLine, isWholeOrderOperation) is { } rejection)
        {
            return rejection;
        }

        if (isWholeOrderOperation)
        {
            return ApplyWholeOrderDiscountAmount(keypadBuffer);
        }

        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        if (value > line.GrossAmount)
        {
            return Status("pos.status.discountAmountTooHigh");
        }

        if (!_cart.SetLineDiscountAmount(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.lineDiscountUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    public PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
    {
        if (GetDiscountRejection(selectedLine, isWholeOrderOperation) is { } rejection)
        {
            return rejection;
        }

        if (isWholeOrderOperation)
        {
            return ApplyWholeOrderDiscountPercent(keypadBuffer);
        }

        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        return ApplySelectedLineDiscountPercent(line, value);
    }

    public PosTerminalWorkflowResult ApplyQuickDiscountPercent(CartLine? selectedLine, string? value, bool isWholeOrderOperation)
    {
        // 先于档位校验：行模式下选中退货行，无论点哪一档都不可能成功。
        if (GetDiscountRejection(selectedLine, isWholeOrderOperation) is { } rejection)
        {
            return rejection;
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var discountPercent) ||
            discountPercent < 0m)
        {
            return Status("pos.status.invalidKeypadValue");
        }

        if (discountPercent > 100m)
        {
            return Status("pos.status.discountPercentOutOfRange");
        }

        return isWholeOrderOperation
            ? ApplyWholeOrderDiscountPercent(discountPercent)
            : ApplySelectedLineDiscountPercentCore(selectedLine, discountPercent);
    }

    public PosTerminalWorkflowResult ClearCart()
    {
        // 清空即换单：清空前仍在补查的商品，结果回来后不能落进清空后的购物车。
        AdvanceCartGeneration("clear-cart");
        _cart.Clear();
        return Status("pos.status.cartCleared");
    }

    public PosTerminalWorkflowResult GuardPayment()
    {
        if (_cart.HasNonIntegerQuantity)
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        if (_cart.HasZeroPriceLine)
        {
            return Status("cart.status.zeroPriceItem");
        }

        // 校验通过才会真正进入收款；被拦下时收银员还在收银页，在途补查照常加购，所以放在所有拦截之后。
        AdvanceCartGeneration("payment-started");
        return new PosTerminalWorkflowResult { PaymentAllowed = true };
    }

    private PosTerminalWorkflowResult ApplyScanMatches(
        PosSessionState session,
        IReadOnlyList<SellableItemDto> matches,
        string submittedScanText,
        bool allowAutoAdd,
        string? traceId = null,
        string? scanSource = null,
        bool queueConflicts = false)
    {
        var selectedItem = matches.FirstOrDefault();
        if (selectedItem is null)
        {
            return Status("pos.status.noLocalMatch") with
            {
                Matches = matches,
                MatchesPopupOpen = false
            };
        }

        if (allowAutoAdd && (matches.Count == 1 || IsExactLookup(selectedItem, submittedScanText)))
        {
            var addResult = AddItem(session, selectedItem, clearScanText: true, closeMatchesPopup: false, "scan-auto-add", traceId);
            return addResult with
            {
                Matches = matches,
                SelectedItem = selectedItem,
                MatchesPopupOpen = false
            };
        }

        if (queueConflicts)
        {
            return EnqueuePendingChoice(session, matches, submittedScanText, scanSource, traceId);
        }

        return Status("pos.status.multipleMatches", matches.Count) with
        {
            Matches = matches,
            SelectedItem = selectedItem,
            MatchesPopupOpen = true
        };
    }

    /// <summary>
    /// 扫码枪扫到多商品码：记入待选队列，不碰搜索下拉、不加购。之后继续扫的商品照常加购，待选一直保留到收银员选定或放弃。
    /// 入队发生在去重（ScannerInputDuplicateGuard）之后，这里每一次调用都是一次真实扫码，所以不再做任何合并。
    /// </summary>
    private PosTerminalWorkflowResult EnqueuePendingChoice(
        PosSessionState session,
        IReadOnlyList<SellableItemDto> matches,
        string submittedScanText,
        string? scanSource,
        string? traceId)
    {
        var source = string.IsNullOrWhiteSpace(scanSource) ? "scan" : scanSource;
        var entry = _pendingChoices.TryEnqueue(submittedScanText, source, traceId, session.StoreCode, matches);
        if (entry is null)
        {
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}pending choice rejected reason=queue-full capacity={PendingScanChoiceQueue.Capacity} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(submittedScanText)} source={source} candidates={matches.Count}");
            return Status("pos.status.pendingChoiceFull", PendingScanChoiceQueue.Capacity) with
            {
                Matches = [],
                SelectedItem = null,
                MatchesPopupOpen = false,
                ScanConflictHandled = true
            };
        }

        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(traceId)}pending choice created id={entry.Id} queueLength={_pendingChoices.Count} candidates={matches.Count} sameAsPrevious={FormatBool(entry.SameBarcodeAsPrevious)} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(submittedScanText)} source={source} storeCode={session.StoreCode} candidateProducts={FormatCandidateProducts(matches)}");
        return Status("pos.status.pendingChoiceQueued", matches.Count, _pendingChoices.Count) with
        {
            // 清掉上一次手工搜索遗留的下拉与候选，避免两套候选同时出现。
            Matches = [],
            SelectedItem = null,
            MatchesPopupOpen = false,
            ScanConflictHandled = true
        };
    }

    public PosTerminalWorkflowResult ResolvePendingChoice(PosSessionState session, long choiceId, SellableItemDto item)
    {
        using var uiOperation = _uiPriorityCoordinator.BeginUiOperation("scan-choice-select");
        var entry = _pendingChoices.TryGet(choiceId);
        if (entry is null || !entry.Candidates.Contains(item))
        {
            // 条目已被选定 / 放弃 / 清空（例如重复点击），或点的不是这一条的候选：不加购，只提示。
            ConsoleLog.Write(
                "PosScan",
                $"pending choice resolve skipped id={choiceId} reason={(entry is null ? "not-found" : "not-candidate")} productCode={LogValue(item.ProductCode)}");
            return Status("pos.status.pendingChoiceGone");
        }

        // 选定沿用手工选择的全局合并规则（同商品那一行数量 +1，否则追加末尾），与「立即选择」的既有行为一致。
        var addResult = AddItem(session, item, clearScanText: false, closeMatchesPopup: false, "scan-choice-select", entry.TraceId);
        if (!string.Equals(addResult.StatusKey, "pos.status.added", StringComparison.Ordinal))
        {
            // 加购失败（如数量非整数）：条目保留，收银员可改选或放弃。
            return addResult;
        }

        _pendingChoices.Remove(choiceId);
        var remaining = _pendingChoices.Count;
        var waitedMs = Math.Max(0, (long)(_timeProvider.GetLocalNow() - entry.CreatedAt).TotalMilliseconds);
        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(entry.TraceId)}pending choice resolved id={entry.Id} waitedMs={waitedMs} productCode={LogValue(item.ProductCode)} lookupCode={LogValue(item.LookupCode)} lineQuantity={FormatAmount(addResult.SelectedCartLine?.Quantity ?? 0m)} queueLength={remaining}");
        return addResult with
        {
            StatusKey = remaining > 0 ? "pos.status.pendingChoiceResolved" : "pos.status.pendingChoiceResolvedLast",
            StatusArgs = [item.DisplayName, addResult.SelectedCartLine?.Quantity ?? 0m, remaining]
        };
    }

    public bool AbandonPendingChoice(long choiceId, string reason)
    {
        var entry = _pendingChoices.TryGet(choiceId);
        if (entry is null || !_pendingChoices.Remove(choiceId))
        {
            return false;
        }

        LogAbandoned(entry, reason, _pendingChoices.Count);
        return true;
    }

    public int AbandonAllPendingChoices(string reason)
    {
        var removed = _pendingChoices.Clear();
        foreach (var entry in removed)
        {
            LogAbandoned(entry, reason, queueLength: 0);
        }

        return removed.Count;
    }

    private void LogAbandoned(PendingScanChoice entry, string reason, int queueLength)
    {
        var waitedMs = Math.Max(0, (long)(_timeProvider.GetLocalNow() - entry.CreatedAt).TotalMilliseconds);
        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(entry.TraceId)}pending choice abandoned id={entry.Id} reason={reason} waitedMs={waitedMs} candidates={entry.Candidates.Count} candidateProducts={FormatCandidateProducts(entry.Candidates)} queueLength={queueLength}");
    }

    private static string FormatCandidateProducts(IReadOnlyList<SellableItemDto> candidates)
    {
        return string.Join(',', candidates.Take(5).Select(candidate => LogValue(candidate.ProductCode)));
    }

    private PosTerminalWorkflowResult AddItem(
        PosSessionState session,
        SellableItemDto item,
        bool clearScanText,
        bool closeMatchesPopup,
        string operation,
        string? traceId = null,
        bool refreshRemoteAfterAdd = true)
    {
        var totalStopwatch = Stopwatch.StartNew();
        if (!PosCartService.IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            totalStopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}cart add skipped operation={operation} storeCode={session.StoreCode} lookupCode={LogValue(item.LookupCode)} productCode={LogValue(item.ProductCode)} reason=quantity-must-be-integer totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return Status("cart.status.quantityMustBeInteger");
        }

        CartLine line;
        var addStopwatch = Stopwatch.StartNew();
        try
        {
            // 扫码自动加购走连续合并规则，其他入口继续沿用全局合并逻辑。
            line = string.Equals(operation, "scan-auto-add", StringComparison.Ordinal)
                ? _cart.AddConsecutiveItem(item)
                : _cart.AddItem(item);
        }
        catch (InvalidOperationException)
        {
            addStopwatch.Stop();
            totalStopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}cart add failed operation={operation} storeCode={session.StoreCode} lookupCode={LogValue(item.LookupCode)} productCode={LogValue(item.ProductCode)} reason=quantity-must-be-integer addItemElapsedMs={addStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return Status("cart.status.quantityMustBeInteger");
        }
        addStopwatch.Stop();

        if (refreshRemoteAfterAdd)
        {
            BeginRemoteLookup(session, line, item);
        }

        totalStopwatch.Stop();
        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(traceId)}cart add completed operation={operation} storeCode={session.StoreCode} lookupCode={LogValue(item.LookupCode)} productCode={LogValue(item.ProductCode)} lineQuantity={FormatAmount(line.Quantity)} cartLines={_cart.Lines.Count} addItemElapsedMs={addStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return Status("pos.status.added", item.DisplayName) with
        {
            SelectedCartLine = line,
            ClearScanText = clearScanText,
            MatchesPopupOpen = closeMatchesPopup ? false : null,
            TouchKeyboardOpen = false
        };
    }

    private static bool IsLocalMiss(PosTerminalWorkflowResult localResult)
    {
        return string.Equals(localResult.StatusKey, "pos.status.noLocalMatch", StringComparison.Ordinal) &&
            localResult.Matches is not null &&
            localResult.Matches.Count == 0;
    }

    private async Task<PosTerminalWorkflowResult> TryAddRemoteLookupMissAsync(
        PosSessionState session,
        string scanText,
        string source,
        string? traceId,
        PosTerminalWorkflowResult localResult,
        long cartGeneration,
        PosScanHooks? hooks,
        CancellationToken cancellationToken)
    {
        if (!session.IsOnline)
        {
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}remote lookup skipped after local miss storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source} reason=offline");
            return localResult;
        }

        if (_remoteLookupRefreshAsync is null)
        {
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}remote lookup skipped after local miss storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source} reason=refresh-handler-missing");
            return localResult;
        }

        var stopwatch = Stopwatch.StartNew();
        var key = new RemoteLookupKey(NormalizeIdentity(session.StoreCode), NormalizeIdentity(scanText));
        var pendingScan = new PendingRemoteScan(key, cartGeneration);
        Task<RemoteLookupRefreshResult>? lookupTask = null;
        var startedLookup = false;
        var registeredScan = false;
        try
        {
            lock (_remoteLookupGate)
            {
                // 同码同世代只登记一次：补查进行中收银员再扫同一个码，不再叠加第二次加购（否则一个商品会加成两件）。
                // 世代不同（清空 / 换单后重扫）则不算重复，仍然共享同一次远程请求，但各自按自己的世代决定是否加购。
                registeredScan = _pendingRemoteScans.Add(pendingScan);
                if (registeredScan && !_pendingRemoteLookupTasks.TryGetValue(key, out lookupTask))
                {
                    lookupTask = ExecuteRemoteLookupWithTimeoutAsync(session.StoreCode, scanText, cancellationToken);
                    _pendingRemoteLookupTasks[key] = lookupTask;
                    startedLookup = true;
                }
            }

            if (!registeredScan)
            {
                // 不等待、不加购，直接告诉收银员「这个码正在查询」，避免界面长时间无反应又被反复扫码。
                ConsoleLog.Write(
                    "PosScan",
                    $"{FormatTraceId(traceId)}remote lookup duplicate scan ignored storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source} cartGeneration={cartGeneration}");
                return Status("pos.status.remoteLookupInProgress");
            }

            ConsoleLog.Write(
                "PosScan",
                startedLookup
                    ? $"{FormatTraceId(traceId)}remote lookup dispatch after local miss storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source}"
                    : $"{FormatTraceId(traceId)}remote lookup joined after local miss storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source}");

            // 补查最长要等 2 秒：发起的同一时刻先给收银员反馈，而不是等结果回来才有动静。
            InvokeHook(hooks?.OnInterimResult, Status("pos.status.remoteLookupStarted"), "interim-result");

            var result = await lookupTask!.WaitAsync(cancellationToken);
            stopwatch.Stop();

            if (result.Updated && result.Item is not null)
            {
                // 商品信息本身是最新的，无论这一次要不要加购都先回写本地索引：被丢弃后收银员重扫即可本地命中。
                await Task.Run(() => _priceIndex.Upsert(result.Item), cancellationToken);
                PosTerminalWorkflowResult? addResult = null;
                lock (_remoteLookupGate)
                {
                    // ponytail: 复用现有远程查询锁串行化短回写；并发吞吐成为瓶颈时再按 RemoteLookupKey 分锁。
                    // 世代校验与加购放在同一临界区内，二者之间不会再插进来清空 / 收款等操作。
                    if (cartGeneration == Volatile.Read(ref _cartGeneration))
                    {
                        InvokeHook(hooks?.OnBeforeRemoteApply, "before-remote-apply");
                        // 本地未命中后远端已返回最新商品，这里直接加购，避免加购后再重复排队远端刷新。
                        addResult = AddItem(
                            session,
                            result.Item,
                            clearScanText: true,
                            closeMatchesPopup: false,
                            "scan-auto-add",
                            traceId,
                            refreshRemoteAfterAdd: false);
                    }
                }

                CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs());
                if (addResult is null)
                {
                    // 扫码之后购物车已被清空 / 已开始收款 / 已换单 / 会话已变：这件商品不属于现在的购物车，丢弃并提示重扫。
                    ConsoleLog.Write(
                        "PosScan",
                        $"{FormatTraceId(traceId)}remote lookup local miss discarded storeCode={session.StoreCode} lookupCode={LogValue(scanText)} productCode={LogValue(result.Item.ProductCode)} scanGeneration={cartGeneration} currentGeneration={Volatile.Read(ref _cartGeneration)} elapsedMs={stopwatch.ElapsedMilliseconds}");
                    return Status("pos.status.remoteLookupDiscarded");
                }

                ConsoleLog.Write(
                    "PosScan",
                    $"{FormatTraceId(traceId)}remote lookup local miss added storeCode={session.StoreCode} lookupCode={LogValue(scanText)} productCode={LogValue(result.Item.ProductCode)} elapsedMs={stopwatch.ElapsedMilliseconds}");
                return addResult with
                {
                    Matches = [result.Item],
                    SelectedItem = result.Item,
                    MatchesPopupOpen = false
                };
            }

            if (result.Deleted)
            {
                await Task.Run(() => _priceIndex.RemoveLookup(result.StoreCode, result.LookupCode), cancellationToken);
                CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs());
            }

            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}remote lookup local miss not found storeCode={session.StoreCode} lookupCode={LogValue(scanText)} deletedCount={result.DeletedCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return localResult;
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}remote lookup local miss timeout storeCode={session.StoreCode} lookupCode={LogValue(scanText)} timeoutMs={RemoteLookupTimeout.TotalMilliseconds:0} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            return localResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"{FormatTraceId(traceId)}remote lookup local miss failed storeCode={session.StoreCode} lookupCode={LogValue(scanText)} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            return localResult;
        }
        finally
        {
            if (registeredScan)
            {
                lock (_remoteLookupGate)
                {
                    // 无论成功、失败、超时还是被取消，都要释放「正在查询」登记，否则这个码在本世代内再也扫不进去。
                    _pendingRemoteScans.Remove(pendingScan);
                    if (startedLookup &&
                        lookupTask is not null &&
                        _pendingRemoteLookupTasks.TryGetValue(key, out var pendingTask) &&
                        ReferenceEquals(pendingTask, lookupTask))
                    {
                        _pendingRemoteLookupTasks.Remove(key);
                    }
                }
            }
        }
    }

    private static void InvokeHook<T>(Action<T>? hook, T argument, string hookName)
    {
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(argument);
        }
        catch (Exception ex)
        {
            // UI 回调出错只记日志，不能让一次提示失败把扫码加购也带崩。
            ConsoleLog.Write("PosScan", $"scan hook failed hook={hookName} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void InvokeHook(Action? hook, string hookName)
    {
        if (hook is null)
        {
            return;
        }

        try
        {
            hook();
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("PosScan", $"scan hook failed hook={hookName} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<RemoteLookupRefreshResult> ExecuteRemoteLookupWithTimeoutAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken)
    {
        // CancelAfter 不接受 TimeProvider，改为独立的超时 CTS 再与外部令牌链接，语义不变。
        using var timeoutCts = new CancellationTokenSource(RemoteLookupTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        return await _remoteLookupRefreshAsync!(storeCode, lookupCode, linkedCts.Token);
    }

    private PosTerminalWorkflowResult ApplyWholeOrderDiscountAmount(string keypadBuffer)
    {
        if (!TryGetOrderDiscountKeypadValue(keypadBuffer, out var value, out var failure))
        {
            return failure;
        }

        // 上限是销售行毛额合计，不是含退货行负数的 TotalAmount（与 PosCartService.SetOrderDiscountAmount 的口径一致）。
        if (value > _cart.OrderDiscountableAmount)
        {
            return Status("pos.status.discountAmountTooHigh");
        }

        if (!_cart.SetOrderDiscountAmount(value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.orderDiscountUpdated") with
        {
            WholeOrderOperation = false,
            ClearKeypadBuffer = true
        };
    }

    private PosTerminalWorkflowResult ApplyWholeOrderDiscountPercent(string keypadBuffer)
    {
        if (!TryGetOrderDiscountKeypadValue(keypadBuffer, out var value, out var failure))
        {
            return failure;
        }

        return ApplyWholeOrderDiscountPercent(value);
    }

    private PosTerminalWorkflowResult ApplyWholeOrderDiscountPercent(decimal value)
    {
        if (_cart.IsEmpty)
        {
            return Status("pos.status.selectCartLine");
        }

        if (value > 100m)
        {
            return Status("pos.status.discountPercentOutOfRange");
        }

        if (!_cart.SetOrderDiscountPercent(value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.orderDiscountUpdated") with
        {
            WholeOrderOperation = false,
            ClearKeypadBuffer = true
        };
    }

    private PosTerminalWorkflowResult ApplySelectedLineDiscountPercentCore(CartLine? selectedLine, decimal value)
    {
        if (selectedLine is null)
        {
            return Status("pos.status.selectCartLine");
        }

        return ApplySelectedLineDiscountPercent((CartLine)selectedLine, value);
    }

    private PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine line, decimal value)
    {
        if (value > 100m)
        {
            return Status("pos.status.discountPercentOutOfRange");
        }

        if (!_cart.SetLineDiscountPercent(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.lineDiscountUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    private bool TryGetSelectedLineKeypadValue(
        CartLine? selectedLine,
        string keypadBuffer,
        out CartLine line,
        out decimal value,
        out PosTerminalWorkflowResult failure)
    {
        value = 0m;

        if (selectedLine is null)
        {
            line = null!;
            failure = Status("pos.status.selectCartLine");
            return false;
        }

        line = selectedLine;
        if (!TryGetKeypadValue(keypadBuffer, out value))
        {
            failure = Status("pos.status.invalidKeypadValue");
            return false;
        }

        failure = new PosTerminalWorkflowResult();
        return true;
    }

    private bool TryGetOrderDiscountKeypadValue(
        string keypadBuffer,
        out decimal value,
        out PosTerminalWorkflowResult failure)
    {
        value = 0m;

        if (_cart.IsEmpty)
        {
            failure = Status("pos.status.selectCartLine");
            return false;
        }

        if (!TryGetKeypadValue(keypadBuffer, out value))
        {
            failure = Status("pos.status.invalidKeypadValue");
            return false;
        }

        failure = new PosTerminalWorkflowResult();
        return true;
    }

    private static bool TryGetKeypadValue(string keypadBuffer, out decimal value)
    {
        return decimal.TryParse(keypadBuffer, NumberStyles.Number, CultureInfo.InvariantCulture, out value) &&
            value >= 0m;
    }

    private void BeginRemoteLookup(PosSessionState session, CartLine line, SellableItemDto item)
    {
        if (!session.IsOnline)
        {
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup skipped storeCode={session.StoreCode} lookupCode={LogValue(item.LookupCode)} productCode={LogValue(item.ProductCode)} reason=offline");
            return;
        }

        if (_remoteLookupRefreshAsync is null)
        {
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup skipped storeCode={session.StoreCode} lookupCode={LogValue(item.LookupCode)} productCode={LogValue(item.ProductCode)} reason=refresh-handler-missing");
            return;
        }

        var snapshot = new RemoteLookupCartSnapshot(
            line,
            session.StoreCode,
            item.LookupCode,
            item.ProductCode,
            item.ReferenceCode);
        var key = RemoteLookupKey.FromSnapshot(snapshot);
        lock (_remoteLookupGate)
        {
            if (!_pendingRemoteLookups.Add(key))
            {
                ConsoleLog.Write(
                    "PosScan",
                    $"remote lookup duplicate skipped storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode}");
                return;
            }
        }

        ConsoleLog.Write(
            "PosScan",
            $"remote lookup queued storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} productCode={snapshot.ProductCode} referenceCode={snapshot.ReferenceCode ?? "<null>"} cartLines={_cart.Lines.Count}");
        // 保留 WPF UI 同步上下文，远程结果才能安全回写购物车集合。
        _ = RefreshRemoteLookupAsync(snapshot, key);
    }

    private async Task RefreshRemoteLookupAsync(RemoteLookupCartSnapshot snapshot, RemoteLookupKey key)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _uiPriorityCoordinator.WaitForUiIdleAsync();
            var catalogSyncActive = _isCatalogSyncActive();
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup dispatch storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} productCode={snapshot.ProductCode} catalogSyncActive={FormatBool(catalogSyncActive)} elapsedMs={stopwatch.ElapsedMilliseconds}");

            using var timeoutCts = new CancellationTokenSource(RemoteLookupTimeout, _timeProvider);
            var result = await _remoteLookupRefreshAsync!(
                snapshot.StoreCode,
                snapshot.LookupCode,
                timeoutCts.Token);
            var remoteResultElapsedMs = stopwatch.ElapsedMilliseconds;

            // 网络返回后再次让扫码输入优先，空闲后再更新索引和界面。
            await _uiPriorityCoordinator.WaitForUiIdleAsync();
            var indexUpdateStopwatch = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                if (result.Updated && result.Item is not null)
                {
                    _priceIndex.Upsert(result.Item);
                }
                else if (result.Deleted)
                {
                    _priceIndex.RemoveLookup(result.StoreCode, result.LookupCode);
                }
            });

            indexUpdateStopwatch.Stop();
            var uiApplyStopwatch = Stopwatch.StartNew();

            if (result.Updated && result.Item is not null)
            {
                if (CanApplyRemoteItemToCartLine(snapshot, result.Item))
                {
                    var updatedCount = UpdateMatchingCartLinesFromRemote(snapshot, result.Item);
                    ConsoleLog.Write(
                        "PosScan",
                        $"remote lookup cart update storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} productCode={snapshot.ProductCode} referenceCode={snapshot.ReferenceCode ?? "<null>"} updated={FormatBool(updatedCount > 0)} updatedCount={updatedCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
                else
                {
                    ConsoleLog.Write(
                        "PosScan",
                        $"remote lookup ignored for cart storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} localProductCode={snapshot.ProductCode} localReferenceCode={snapshot.ReferenceCode ?? "<null>"} remoteProductCode={result.Item.ProductCode} remoteReferenceCode={result.Item.ReferenceCode ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
            }
            else if (result.Deleted)
            {
                ConsoleLog.Write(
                    "PosScan",
                    $"remote lookup deleted local cache only storeCode={result.StoreCode} lookupCode={result.LookupCode} deletedCount={result.DeletedCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs());
            uiApplyStopwatch.Stop();
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup apply completed storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} updated={FormatBool(result.Updated)} deleted={FormatBool(result.Deleted)} remoteResultElapsedMs={remoteResultElapsedMs} indexUpdateElapsedMs={indexUpdateStopwatch.ElapsedMilliseconds} uiApplyElapsedMs={uiApplyStopwatch.ElapsedMilliseconds} totalElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup timeout storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} timeoutMs={RemoteLookupTimeout.TotalMilliseconds:0} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup failed storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
        finally
        {
            lock (_remoteLookupGate)
            {
                _pendingRemoteLookups.Remove(key);
            }
        }
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        return _reloadCatalogAsync is null
            ? _priceIndex.Items
            : await _reloadCatalogAsync(cancellationToken);
    }

    private int UpdateMatchingCartLinesFromRemote(RemoteLookupCartSnapshot snapshot, SellableItemDto item)
    {
        var normalizedLookupCode = CartLine.NormalizeLookupCode(snapshot.LookupCode);
        var lines = _cart.Lines
            .Where(line =>
                !line.IsReturnLine &&
                !line.IsOpenItem &&
                string.Equals(line.StoreCode, snapshot.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line.LookupCodeNormalized, normalizedLookupCode, StringComparison.Ordinal) &&
                EqualsIdentity(line.ProductCode, snapshot.ProductCode) &&
                EqualsIdentity(line.ReferenceCode, snapshot.ReferenceCode))
            .ToList();

        var updatedCount = 0;
        foreach (var line in lines)
        {
            // 非连续扫码会产生多条同码销售行，同一次远程刷新需要同步更新这些行。
            if (_cart.UpdateLineFromRemote(line, item))
            {
                updatedCount++;
            }
        }

        return updatedCount;
    }

    private static bool CanApplyRemoteItemToCartLine(RemoteLookupCartSnapshot snapshot, SellableItemDto item)
    {
        return EqualsIdentity(snapshot.StoreCode, item.StoreCode) &&
            EqualsIdentity(snapshot.ProductCode, item.ProductCode) &&
            EqualsIdentity(snapshot.ReferenceCode, item.ReferenceCode);
    }

    private static bool EqualsIdentity(string? left, string? right)
    {
        return string.Equals(NormalizeIdentity(left), NormalizeIdentity(right), StringComparison.Ordinal);
    }

    private static string NormalizeIdentity(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool IsExactLookup(SellableItemDto item, string query)
    {
        var normalized = query.Trim();
        return string.Equals(item.Barcode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.LookupCode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ItemNumber, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static PosTerminalWorkflowResult Status(string key, params object[] args)
    {
        return new PosTerminalWorkflowResult
        {
            StatusKey = key,
            StatusArgs = args
        };
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatTraceId(string? traceId)
    {
        return string.IsNullOrWhiteSpace(traceId) ? string.Empty : $"traceId={traceId} ";
    }

    private static string FormatAmount(decimal value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private sealed record RemoteLookupCartSnapshot(
        CartLine Line,
        string StoreCode,
        string LookupCode,
        string ProductCode,
        string? ReferenceCode);

    private sealed record RemoteLookupKey(string StoreCode, string LookupCode)
    {
        public static RemoteLookupKey FromSnapshot(RemoteLookupCartSnapshot snapshot)
        {
            return new RemoteLookupKey(
                NormalizeIdentity(snapshot.StoreCode),
                NormalizeIdentity(snapshot.LookupCode));
        }
    }

    // 一次「等待补查结果后加购」的扫码：商品码 + 发起时的购物车世代。同值即视为重复扫码。
    private sealed record PendingRemoteScan(RemoteLookupKey Key, long CartGeneration);
}
