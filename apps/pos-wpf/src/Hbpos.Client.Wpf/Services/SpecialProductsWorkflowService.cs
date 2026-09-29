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

    Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<SpecialProductDownloadProgress>? progress = null);

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

    public async Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<SpecialProductDownloadProgress>? progress = null)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var result = await specialProductService.DownloadSpecialProductsAsync(
            normalizedStoreCode,
            cancellationToken,
            progress);

        await RefreshIndexAsync(normalizedStoreCode, cancellationToken);
        var specialItems = await catalogRepository.LoadSpecialProductItemsAsync(normalizedStoreCode, cancellationToken);
        UpdateLoadedResult(normalizedStoreCode, specialItems);
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
}
