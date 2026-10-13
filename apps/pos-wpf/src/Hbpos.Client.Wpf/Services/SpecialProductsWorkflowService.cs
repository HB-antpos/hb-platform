using System.Diagnostics;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public sealed record SpecialProductsLoadResult(
    string StoreCode,
    IReadOnlyList<SellableItemDto> SpecialItems);

public sealed record SpecialProductsSearchResult(
    string StoreCode,
    string SearchText,
    IReadOnlyList<SellableItemDto> Items);

public sealed record SpecialProductsDownloadWorkflowResult(
    SpecialProductDownloadResult DownloadResult,
    IReadOnlyList<SellableItemDto> SpecialItems);

public sealed record SpecialProductsMutationWorkflowResult(
    string StoreCode,
    string ProductCode,
    bool IsSpecialProduct,
    IReadOnlyList<SellableItemDto> SpecialItems);

public sealed record SpecialProductsPriceWorkflowResult(
    string StoreCode,
    string ProductCode,
    decimal RetailPrice,
    IReadOnlyList<SellableItemDto> SpecialItems);

/// <summary>
/// 特殊商品改价的业务规则，与服务端口径一致：大于 0、不超过 99999.99、最多两位小数。
/// 视图模型用它决定「保存」是否可用，工作流服务用它在发请求前兜底。
/// </summary>
public static class SpecialProductPriceRules
{
    public const decimal MaxRetailPrice = 99999.99m;

    public static bool IsValid(decimal price) =>
        price > 0m && price <= MaxRetailPrice && decimal.Round(price, 2) == price;
}

/// <summary>撤销加购的结果：是否改动了购物车、是否把整行删掉。</summary>
public sealed record SpecialProductsUndoAddResult(bool Changed, bool RemovedLine);

public sealed record SpecialProductsReorderWorkflowResult(
    string StoreCode,
    IReadOnlyList<SellableItemDto> SpecialItems,
    string FocusProductCode);

public sealed record SpecialProductsAddToCartResult(
    CartLine Line,
    int CartLineCount);

public interface ISpecialProductsWorkflowService
{
    Task<SpecialProductsLoadResult> PreloadAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    Task<SpecialProductsLoadResult> EnsureLoadedAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    Task<SpecialProductsLoadResult> LoadAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    SpecialProductsSearchResult Search(string storeCode, string searchText);

    Task<SpecialProductsSearchResult> SearchAsync(
        string storeCode,
        string searchText,
        CancellationToken cancellationToken = default);

    SpecialProductsAddToCartResult AddToCart(SellableItemDto item);

    // 中文注释：撤销一次加购；默认实现按"不支持"处理，既有测试替身无需实现。
    SpecialProductsUndoAddResult UndoAddToCart(CartLine line, decimal addedQuantity)
    {
        throw new NotSupportedException("Undo add-to-cart is not supported by this workflow.");
    }

    // 中文注释：onSpecialItemsReady 在本地库写完、特殊商品列表读出后立刻回调（早于整体重建价格索引），
    // 让页面不必等索引重建就能显示下载结果；方法返回时索引仍已完整重建、缓存已就绪。
    // 回调在 await 的延续上同步执行（视图模型调用时即 UI 线程）。
    Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<SpecialProductDownloadProgress>? progress = null,
        Action<IReadOnlyList<SellableItemDto>>? onSpecialItemsReady = null);

    Task<SpecialProductsMutationWorkflowResult> MarkSpecialProductAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default);

    Task<SpecialProductsReorderWorkflowResult?> ReorderAsync(
        string storeCode,
        IReadOnlyList<SellableItemDto> currentItems,
        string productCode,
        int delta,
        CancellationToken cancellationToken = default);

    // 中文注释：修改特殊商品门店零售价（必须在线）；默认实现按"不支持"处理，既有测试替身无需实现。
    Task<SpecialProductsPriceWorkflowResult> UpdateSpecialProductPriceAsync(
        string storeCode,
        string productCode,
        decimal retailPrice,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Special product price update is not supported by this workflow.");
    }
}

public sealed class SpecialProductsWorkflowService(
    LocalSellableItemIndex priceIndex,
    PosCartService cart,
    ILocalCatalogRepository catalogRepository,
    ISpecialProductService specialProductService) : ISpecialProductsWorkflowService
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Task<SpecialProductsLoadResult>? _preloadTask;
    private string? _preloadStoreCode;
    private SpecialProductsLoadResult? _loadedResult;
    private string? _loadedStoreCode;

    public Task<SpecialProductsLoadResult> PreloadAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (IsLoadedForStore(normalizedStoreCode))
        {
            Log($"preload skipped store={normalizedStoreCode} reason=already-loaded");
            return Task.FromResult(_loadedResult!);
        }

        if (_preloadTask is null ||
            _preloadTask.IsCompleted ||
            !string.Equals(_preloadStoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            Log($"preload start store={normalizedStoreCode}");
            _preloadStoreCode = normalizedStoreCode;
            _preloadTask = LoadSpecialProductsAsync(normalizedStoreCode, forceReload: false, cancellationToken);
        }

        return _preloadTask;
    }

    public async Task<SpecialProductsLoadResult> EnsureLoadedAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (IsLoadedForStore(normalizedStoreCode))
        {
            Log($"ensure loaded skipped store={normalizedStoreCode} reason=already-loaded");
            return _loadedResult!;
        }

        var preloadTask = _preloadTask;
        if (preloadTask is not null &&
            !preloadTask.IsCompleted &&
            string.Equals(_preloadStoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            Log($"ensure loaded waiting preload store={normalizedStoreCode}");
            var preloadResult = await preloadTask;
            if (IsLoadedForStore(normalizedStoreCode))
            {
                Log($"ensure loaded completed from preload store={normalizedStoreCode}");
                return preloadResult;
            }
        }

        return await LoadSpecialProductsAsync(normalizedStoreCode, forceReload: false, cancellationToken);
    }

    public Task<SpecialProductsLoadResult> LoadAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        return LoadSpecialProductsAsync(NormalizeStoreCode(storeCode), forceReload: true, cancellationToken);
    }

    public SpecialProductsSearchResult Search(string storeCode, string searchText)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return new SpecialProductsSearchResult(normalizedStoreCode, searchText, []);
        }

        return CreateSearchResult(
            normalizedStoreCode,
            searchText,
            priceIndex.Search(normalizedStoreCode, searchText, 80));
    }

    public async Task<SpecialProductsSearchResult> SearchAsync(
        string storeCode,
        string searchText,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return new SpecialProductsSearchResult(normalizedStoreCode, searchText, []);
        }

        var matches = await priceIndex.SearchAsync(normalizedStoreCode, searchText, cancellationToken, 80);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateSearchResult(normalizedStoreCode, searchText, matches);
    }

    private static SpecialProductsSearchResult CreateSearchResult(
        string normalizedStoreCode,
        string searchText,
        IReadOnlyList<SellableItemDto> matches)
    {
        var results = matches
            .Where(item =>
                string.Equals(item.StoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase) &&
                !item.IsSpecialProduct)
            .GroupBy(item => NormalizeProductCode(item.ProductCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(PreferredLookupRank)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.LookupCode, StringComparer.OrdinalIgnoreCase)
                .First())
            .Take(12)
            .ToArray();

        return new SpecialProductsSearchResult(normalizedStoreCode, searchText, results);
    }

    public SpecialProductsAddToCartResult AddToCart(SellableItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var line = cart.AddItem(item);
        return new SpecialProductsAddToCartResult(line, cart.Lines.Count);
    }

    public SpecialProductsUndoAddResult UndoAddToCart(CartLine line, decimal addedQuantity)
    {
        ArgumentNullException.ThrowIfNull(line);

        // 购物车行已被收银台改动或移除时不做任何事。
        if (!cart.Lines.Contains(line) || addedQuantity <= 0m)
        {
            return new SpecialProductsUndoAddResult(false, false);
        }

        // 数量不超过本次加入量就删整行，否则只减回本次加入的数量。
        var removeWholeLine = line.Quantity <= addedQuantity;
        var changed = removeWholeLine
            ? cart.RemoveLine(line)
            : cart.SetLineQuantity(line, line.Quantity - addedQuantity);
        return new SpecialProductsUndoAddResult(changed, changed && removeWholeLine);
    }

    public async Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<SpecialProductDownloadProgress>? progress = null,
        Action<IReadOnlyList<SellableItemDto>>? onSpecialItemsReady = null)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);

        // 有「列表就绪」回调时，把服务层上报的 Completed(100%) 扣到列表交给调用方之后再放出，
        // 避免进度条先显示「下载完成」而列表还是空的；没有回调时原样透传，行为与改动前一致。
        var deferringProgress = progress is not null && onSpecialItemsReady is not null
            ? new CompletionDeferringProgress(progress)
            : null;

        SpecialProductDownloadResult result;
        IReadOnlyList<SellableItemDto> specialItems;
        var listStopwatch = new Stopwatch();
        try
        {
            result = await specialProductService.DownloadSpecialProductsAsync(
                normalizedStoreCode,
                cancellationToken,
                (IProgress<SpecialProductDownloadProgress>?)deferringProgress ?? progress);

            // 关键逻辑：下载已写完本地库，先读出特殊商品列表（只读特殊商品，很轻）并立刻交给调用方显示；
            // 整体重建价格索引要读全店可售商品，商品多时要好几秒，放在显示之后。
            listStopwatch.Start();
            specialItems = await catalogRepository.LoadSpecialProductItemsAsync(normalizedStoreCode, cancellationToken);

            // 缓存时机：本地库已变，旧缓存立刻作废（否则此时重新进入页面会拿旧缓存把已显示的新列表盖回去）；
            // 新缓存等索引重建成功后再写入，保证缓存里的特殊商品不会比索引新，也不会比页面显示的旧。
            ClearLoadedResult(normalizedStoreCode);
            NotifySpecialItemsReady(onSpecialItemsReady, normalizedStoreCode, specialItems);
            listStopwatch.Stop();
        }
        finally
        {
            // 无论成功还是读列表失败/被取消，都放出被扣住的 Completed，避免进度条停在「下载中 100%」。
            deferringProgress?.Release();
        }

        var indexStopwatch = Stopwatch.StartNew();
        try
        {
            // 关键逻辑：重建放到线程池执行。仓储读库的「Async」实为同步完成（Microsoft.Data.Sqlite），
            // 建索引又是纯 CPU，若在 UI 线程上内联执行会一直占住它，刚显示的列表要等重建结束才能重绘；
            // 与 ShellCatalogService 加载目录同样走 Task.Run。ReplaceAll 在锁外建好再交换引用，可从任意线程调用。
            await Task.Run(() => RefreshIndexAsync(normalizedStoreCode, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            // 重建失败/被取消：本地库已更新但内存索引没跟上。缓存保持作废，下次进入页面从本地库重读，
            // 而不是回退成下载前的旧缓存；异常照旧向上抛，由视图模型走「下载失败」提示并复位忙碌状态。
            ClearLoadedResult(normalizedStoreCode);
            indexStopwatch.Stop();
            Log($"download index refresh failed store={normalizedStoreCode} items={specialItems.Count} listReadyElapsedMs={listStopwatch.ElapsedMilliseconds} indexRefreshElapsedMs={indexStopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }

        UpdateLoadedResult(normalizedStoreCode, specialItems);
        indexStopwatch.Stop();
        Log($"download completed store={normalizedStoreCode} items={specialItems.Count} listReadyElapsedMs={listStopwatch.ElapsedMilliseconds} indexRefreshElapsedMs={indexStopwatch.ElapsedMilliseconds}");
        return new SpecialProductsDownloadWorkflowResult(result, specialItems);
    }

    public async Task<SpecialProductsMutationWorkflowResult> MarkSpecialProductAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedProductCode = NormalizeProductCode(productCode);

        var result = await specialProductService.MarkSpecialProductAsync(
            normalizedStoreCode,
            normalizedProductCode,
            isSpecialProduct,
            cancellationToken);

        IReadOnlyList<SellableItemDto> specialItems;
        if (result.ChangedItems.Count > 0)
        {
            foreach (var item in result.ChangedItems)
            {
                priceIndex.Upsert(item);
            }

            specialItems = result.SpecialItems;
        }
        else
        {
            await RefreshIndexAsync(normalizedStoreCode, cancellationToken);
            specialItems = await catalogRepository.LoadSpecialProductItemsAsync(normalizedStoreCode, cancellationToken);
        }

        UpdateLoadedResult(normalizedStoreCode, specialItems);
        return new SpecialProductsMutationWorkflowResult(
            normalizedStoreCode,
            normalizedProductCode,
            isSpecialProduct,
            specialItems);
    }

    public async Task<SpecialProductsPriceWorkflowResult> UpdateSpecialProductPriceAsync(
        string storeCode,
        string productCode,
        decimal retailPrice,
        CancellationToken cancellationToken = default)
    {
        // 业务规则在服务层兜底：非法价格不发请求（视图模型也会提前拦下）。
        if (!SpecialProductPriceRules.IsValid(retailPrice))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retailPrice),
                retailPrice,
                "Retail price must be greater than 0, at most 99999.99 and have at most two decimals.");
        }

        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedProductCode = NormalizeProductCode(productCode);

        var result = await specialProductService.UpdateSpecialProductPriceAsync(
            normalizedStoreCode,
            normalizedProductCode,
            retailPrice,
            cancellationToken);

        IReadOnlyList<SellableItemDto> specialItems;
        if (result.ChangedItems.Count > 0)
        {
            // 服务端返回了该商品改价后的全部查码项，直接覆盖内存价格索引，收银扫码立即按新价。
            foreach (var item in result.ChangedItems)
            {
                priceIndex.Upsert(item);
            }

            specialItems = result.SpecialItems;
        }
        else
        {
            await RefreshIndexAsync(normalizedStoreCode, cancellationToken);
            specialItems = await catalogRepository.LoadSpecialProductItemsAsync(normalizedStoreCode, cancellationToken);
        }

        UpdateLoadedResult(normalizedStoreCode, specialItems);
        return new SpecialProductsPriceWorkflowResult(
            normalizedStoreCode,
            normalizedProductCode,
            retailPrice,
            specialItems);
    }

    public async Task<SpecialProductsReorderWorkflowResult?> ReorderAsync(
        string storeCode,
        IReadOnlyList<SellableItemDto> currentItems,
        string productCode,
        int delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentItems);

        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedProductCode = NormalizeProductCode(productCode);
        var reordered = currentItems.ToList();
        var currentIndex = reordered.FindIndex(item => string.Equals(
            NormalizeProductCode(item.ProductCode),
            normalizedProductCode,
            StringComparison.OrdinalIgnoreCase));
        var nextIndex = currentIndex + delta;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= reordered.Count)
        {
            return null;
        }

        var moved = reordered[currentIndex];
        reordered.RemoveAt(currentIndex);
        reordered.Insert(nextIndex, moved);
        await catalogRepository.SaveSpecialProductOrderAsync(
            normalizedStoreCode,
            reordered.Select(item => item.ProductCode),
            cancellationToken);

        UpdateLoadedResult(normalizedStoreCode, reordered);
        return new SpecialProductsReorderWorkflowResult(
            normalizedStoreCode,
            reordered,
            normalizedProductCode);
    }

    private async Task<SpecialProductsLoadResult> LoadSpecialProductsAsync(
        string normalizedStoreCode,
        bool forceReload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedStoreCode);

        if (!forceReload && IsLoadedForStore(normalizedStoreCode))
        {
            Log($"load skipped store={normalizedStoreCode} reason=already-loaded forceReload={forceReload}");
            return _loadedResult!;
        }

        if (forceReload)
        {
            _preloadTask = null;
            _preloadStoreCode = null;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var lockWaitElapsedMs = 0L;
        try
        {
            Log($"load start store={normalizedStoreCode} forceReload={forceReload}");
            var lockStopwatch = Stopwatch.StartNew();
            await _loadLock.WaitAsync(cancellationToken);
            lockStopwatch.Stop();
            lockWaitElapsedMs = lockStopwatch.ElapsedMilliseconds;
            try
            {
                if (!forceReload && IsLoadedForStore(normalizedStoreCode))
                {
                    totalStopwatch.Stop();
                    Log($"load skipped inside lock store={normalizedStoreCode} reason=already-loaded lockWaitElapsedMs={lockWaitElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
                    return _loadedResult!;
                }

                var loadStopwatch = Stopwatch.StartNew();
                var specialItems = await catalogRepository.LoadSpecialProductItemsAsync(
                    normalizedStoreCode,
                    cancellationToken);
                loadStopwatch.Stop();

                totalStopwatch.Stop();
                UpdateLoadedResult(normalizedStoreCode, specialItems);
                Log($"load completed store={normalizedStoreCode} items={specialItems.Count} lockWaitElapsedMs={lockWaitElapsedMs} loadElapsedMs={loadStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
                return _loadedResult!;
            }
            finally
            {
                _loadLock.Release();
            }
        }
        catch
        {
            totalStopwatch.Stop();
            ClearLoadedResult(normalizedStoreCode);
            Log($"load failed store={normalizedStoreCode} lockWaitElapsedMs={lockWaitElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            throw;
        }
    }

    private async Task RefreshIndexAsync(string normalizedStoreCode, CancellationToken cancellationToken)
    {
        var catalogItems = await catalogRepository.LoadSellableItemsAsync(normalizedStoreCode, cancellationToken);
        var codeConflictItems = await catalogRepository.LoadCodeConflictItemsAsync(normalizedStoreCode, cancellationToken);
        // 与目录加载保持一致：刷新特殊商品后不能丢掉码冲突的备选商品。
        priceIndex.ReplaceAll(CatalogCodeConflictMerger.Merge(catalogItems, codeConflictItems));
    }

    private static void NotifySpecialItemsReady(
        Action<IReadOnlyList<SellableItemDto>>? onSpecialItemsReady,
        string normalizedStoreCode,
        IReadOnlyList<SellableItemDto> specialItems)
    {
        if (onSpecialItemsReady is null)
        {
            return;
        }

        try
        {
            onSpecialItemsReady(specialItems);
        }
        catch (Exception ex)
        {
            // 提前显示只是体验优化：回调失败不能挡住后面的价格索引重建（索引必须跟上已更新的本地库）；
            // 调用方可凭返回值兜底再显示一次，仍失败时由调用方自己的失败路径提示。
            Log($"download special items ready callback failed store={normalizedStoreCode} error={ex.Message}");
        }
    }

    private bool IsLoadedForStore(string normalizedStoreCode)
    {
        return _loadedResult is not null &&
            string.Equals(_loadedStoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateLoadedResult(string normalizedStoreCode, IReadOnlyList<SellableItemDto> specialItems)
    {
        _loadedStoreCode = normalizedStoreCode;
        _loadedResult = new SpecialProductsLoadResult(normalizedStoreCode, specialItems);
    }

    private void ClearLoadedResult(string normalizedStoreCode)
    {
        if (string.Equals(_loadedStoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            _loadedStoreCode = null;
            _loadedResult = null;
        }
    }

    private static int PreferredLookupRank(SellableItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.Barcode) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.Barcode), StringComparison.Ordinal))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(item.ItemNumber) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.ItemNumber), StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }

    private static string NormalizeLookupCode(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("SpecialProducts", message);
    }

    /// <summary>
    /// 扣住下载服务上报的 Completed 阶段，直到 <see cref="Release"/> 才转发给真正的进度接收方；
    /// 其余阶段（准备、下载中、失败）原样转发。服务层只会在全部写库完成后上报一次 Completed。
    /// </summary>
    private sealed class CompletionDeferringProgress(IProgress<SpecialProductDownloadProgress> inner)
        : IProgress<SpecialProductDownloadProgress>
    {
        private readonly object _gate = new();
        private SpecialProductDownloadProgress? _deferred;
        private bool _released;

        public void Report(SpecialProductDownloadProgress value)
        {
            if (value.Stage == SpecialProductDownloadProgressStage.Completed)
            {
                lock (_gate)
                {
                    if (!_released)
                    {
                        _deferred = value;
                        return;
                    }
                }
            }

            inner.Report(value);
        }

        public void Release()
        {
            SpecialProductDownloadProgress? deferred;
            lock (_gate)
            {
                _released = true;
                deferred = _deferred;
                _deferred = null;
            }

            if (deferred is not null)
            {
                inner.Report(deferred);
            }
        }
    }
}
