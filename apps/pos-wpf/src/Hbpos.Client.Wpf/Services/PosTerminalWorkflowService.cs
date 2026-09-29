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
    }

    public event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

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
                var metadataMatches = _priceIndex.FindMetadataExactMatches(session.StoreCode, submittedScanText);
                if (metadataMatches.Count > 0)
                {
                    matches = metadataMatches;
                    matchKind = metadataMatches.Count > 1 ? "metadata-duplicate" : "metadata-only";
                }
            }

            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd, traceId);
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

            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd: !hasDuplicateExactMatch, traceId);
            matchCount = matches.Count;
            autoAdded = result.ClearScanText;
        }

        totalStopwatch.Stop();
        ConsoleLog.Write(
            "PosScan",
            $"{FormatTraceId(traceId)}barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(submittedScanText)} storeCode={session.StoreCode} source={source} hit={matchKind} matchCount={matchCount} autoAdded={FormatBool(autoAdded)} cartLines={_cart.Lines.Count} exactLookupElapsedMs={exactLookupElapsedMs} searchElapsedMs={searchElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");

        return result;
    }

    public async Task<PosTerminalWorkflowResult> ProcessScanAsync(
        PosSessionState session,
        string scanText,
        bool preferExactLookup,
        string source,
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        var localResult = preferExactLookup
            ? ProcessScan(session, scanText, preferExactLookup, source, traceId)
            : await ProcessSearchScanAsync(session, scanText, source, traceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLocalMiss(localResult))
        {
            return localResult;
        }

        return await TryAddRemoteLookupMissAsync(session, scanText, source, traceId, localResult, cancellationToken);
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
            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd: !hasDuplicateExactMatch, traceId);
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

    public PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer)
    {
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

        return new PosTerminalWorkflowResult { PaymentAllowed = true };
    }

    private PosTerminalWorkflowResult ApplyScanMatches(
        PosSessionState session,
        IReadOnlyList<SellableItemDto> matches,
        string submittedScanText,
        bool allowAutoAdd,
        string? traceId = null)
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

        return Status("pos.status.multipleMatches", matches.Count) with
        {
            Matches = matches,
            SelectedItem = selectedItem,
            MatchesPopupOpen = true
        };
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
        Task<RemoteLookupRefreshResult>? lookupTask = null;
        var startedLookup = false;
        try
        {
            lock (_remoteLookupGate)
            {
                if (!_pendingRemoteLookupTasks.TryGetValue(key, out lookupTask))
                {
                    lookupTask = ExecuteRemoteLookupWithTimeoutAsync(session.StoreCode, scanText, cancellationToken);
                    _pendingRemoteLookupTasks[key] = lookupTask;
                    startedLookup = true;
                }
            }

            ConsoleLog.Write(
                "PosScan",
                startedLookup
                    ? $"{FormatTraceId(traceId)}remote lookup dispatch after local miss storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source}"
                    : $"{FormatTraceId(traceId)}remote lookup joined after local miss storeCode={session.StoreCode} lookupCode={LogValue(scanText)} source={source}");

            var result = await lookupTask.WaitAsync(cancellationToken);
            stopwatch.Stop();

            if (result.Updated && result.Item is not null)
            {
                await Task.Run(() => _priceIndex.Upsert(result.Item), cancellationToken);
                PosTerminalWorkflowResult addResult;
                lock (_remoteLookupGate)
                {
                    // ponytail: 复用现有远程查询锁串行化短回写；并发吞吐成为瓶颈时再按 RemoteLookupKey 分锁。
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

                CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs());
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
            if (startedLookup && lookupTask is not null)
            {
                lock (_remoteLookupGate)
                {
                    if (_pendingRemoteLookupTasks.TryGetValue(key, out var pendingTask) &&
                        ReferenceEquals(pendingTask, lookupTask))
                    {
                        _pendingRemoteLookupTasks.Remove(key);
                    }
                }
            }
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

        if (value > _cart.TotalAmount)
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
}
