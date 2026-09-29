using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public delegate IPosTerminalWorkflowService PosTerminalWorkflowFactory(
    Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>> remoteLookupRefreshAsync,
    Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>> reloadCatalogAsync);

public interface IShellCatalogService
{
    bool IsCatalogSyncActive { get; }

    Task ReplacePreviewCatalogAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
        string storeCode,
        bool forceFullDownload,
        IProgress<CatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ShellCatalogService(
    LocalSellableItemIndex priceIndex,
    ILocalCatalogRepository catalogRepository,
    ILocalCatalogSyncService catalogSync,
    PosCartService cart,
    IUiPriorityCoordinator? uiPriorityCoordinator = null,
    ICatalogSyncStatusService? catalogSyncStatus = null) : IShellCatalogService
{
    public ShellCatalogService(
        LocalSellableItemIndex priceIndex,
        ILocalCatalogRepository catalogRepository,
        ILocalCatalogSyncService catalogSync)
        : this(priceIndex, catalogRepository, catalogSync, new PosCartService(), null)
    {
    }

    private readonly object _syncStateGate = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly List<CancellationTokenSource> _regularSyncCts = [];
    private readonly IUiPriorityCoordinator _uiPriorityCoordinator = uiPriorityCoordinator ?? UiPriorityCoordinator.Noop;
    private int _resetRequestCount;
    private int _activeSyncCount;
    // 上一次整表加载进扫码索引的目录；同步结果无变化时直接复用，避免重读几十万行。
    private LoadedCatalog? _loadedCatalog;

    public bool IsCatalogSyncActive => Volatile.Read(ref _activeSyncCount) > 0;

    public async Task ReplacePreviewCatalogAsync(
        IEnumerable<SellableItemDto> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items as IReadOnlyList<SellableItemDto> ?? items.ToArray();
        await catalogRepository.ReplaceSellableItemsAsync(itemList, cancellationToken);
        priceIndex.ReplaceAll(itemList);
        SetLoadedCatalog(storeCode: null, items: null);
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadAndReplaceLocalCatalogOnBackgroundAsync(storeCode, cancellationToken);
        ApplyPromotionRules(result.PromotionRules);
        return result.Items;
    }

    public async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
        string storeCode,
        bool forceFullDownload,
        IProgress<CatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (forceFullDownload)
        {
            return await ResetCatalogAndReloadAsync(storeCode, progress, cancellationToken);
        }

        return await SyncCatalogAndReloadCoreAsync(storeCode, progress, cancellationToken);
    }

    private async Task<IReadOnlyList<SellableItemDto>> ResetCatalogAndReloadAsync(
        string storeCode,
        IProgress<CatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        lock (_syncStateGate)
        {
            _resetRequestCount++;
            foreach (var regularSyncCts in _regularSyncCts)
            {
                regularSyncCts.Cancel();
            }
        }
        try
        {
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                return await RunSyncAndReloadOnBackgroundAsync(
                    storeCode,
                    forceFullDownload: true,
                    progress,
                    cancellationToken);
            }
            finally
            {
                _syncLock.Release();
            }
        }
        finally
        {
            lock (_syncStateGate)
            {
                _resetRequestCount--;
            }
        }
    }

    private async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadCoreAsync(
        string storeCode,
        IProgress<CatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var regularSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_syncStateGate)
        {
            if (_resetRequestCount > 0)
            {
                throw new OperationCanceledException("Catalog sync canceled because catalog reset is pending.");
            }

            _regularSyncCts.Add(regularSyncCts);
        }

        var lockAcquired = false;
        try
        {
            await _syncLock.WaitAsync(regularSyncCts.Token);
            lockAcquired = true;
            lock (_syncStateGate)
            {
                if (_resetRequestCount > 0)
                {
                    throw new OperationCanceledException("Catalog sync canceled because catalog reset is pending.");
                }
            }

            return await RunSyncAndReloadOnBackgroundAsync(
                storeCode,
                forceFullDownload: false,
                progress,
                regularSyncCts.Token);
        }
        finally
        {
            if (lockAcquired)
            {
                _syncLock.Release();
            }

            lock (_syncStateGate)
            {
                _regularSyncCts.Remove(regularSyncCts);
            }
        }
    }

    private async Task<IReadOnlyList<SellableItemDto>> RunSyncAndReloadOnBackgroundAsync(
        string storeCode,
        bool forceFullDownload,
        IProgress<CatalogSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 此方法仅在同步锁内调用，排队任务必须使用上一轮重载后的目录数量计算核对进度。
        var syncProgress = progress is null
            ? null
            : new CatalogSyncProgressSink(progress, priceIndex.Count);
        // 启动、手动下载和数据重置都经过这里，统一记录设置页展示的同步时间。
        catalogSyncStatus?.MarkStarted(storeCode);
        LocalCatalogReloadResult result;
        try
        {
            result = await Task.Run(async () =>
            {
                Interlocked.Increment(ref _activeSyncCount);
                try
                {
                    var syncResult = await catalogSync.FullSyncAsync(storeCode, cancellationToken, syncProgress, forceFullDownload)
                        .ConfigureAwait(false);
                    if (syncResult is { CatalogChanged: false, CodeConflictsChanged: false } &&
                        TryGetLoadedCatalog(storeCode, out var loadedItems))
                    {
                        // 中文注释：目录与冲突候选都没写库时沿用内存扫码索引，省掉整表重读与排序；促销规则仍按库里最新的应用。
                        var promotionRules = await catalogRepository.LoadPromotionRulesAsync(storeCode, cancellationToken)
                            .ConfigureAwait(false);
                        return new LocalCatalogReloadResult(loadedItems, promotionRules);
                    }

                    return await LoadAndReplaceLocalCatalogAsync(storeCode, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeSyncCount);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            catalogSyncStatus?.MarkCanceled(storeCode);
            throw;
        }
        catch (Exception ex)
        {
            catalogSyncStatus?.MarkFailed(storeCode, ex.Message);
            throw;
        }

        var markSucceeded = catalogSyncStatus?.MarkSucceededAsync(storeCode) ?? Task.CompletedTask;
        ApplyPromotionRules(result.PromotionRules);
        await markSucceeded;
        return result.Items;
    }

    private Task<LocalCatalogReloadResult> LoadAndReplaceLocalCatalogOnBackgroundAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => LoadAndReplaceLocalCatalogAsync(storeCode, cancellationToken),
            cancellationToken);
    }

    private async Task<LocalCatalogReloadResult> LoadAndReplaceLocalCatalogAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken)
            .ConfigureAwait(false);
        var cachedItems = await catalogRepository.LoadSellableItemsAsync(storeCode, cancellationToken)
            .ConfigureAwait(false);
        var codeConflictItems = await catalogRepository.LoadCodeConflictItemsAsync(storeCode, cancellationToken)
            .ConfigureAwait(false);
        await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken)
            .ConfigureAwait(false);
        // 内存索引额外带上码冲突的其它商品，扫码命中多条时弹窗选择；返回值仍是目录本身，不影响商品数等统计。
        priceIndex.ReplaceAll(CatalogCodeConflictMerger.Merge(cachedItems, codeConflictItems));
        SetLoadedCatalog(storeCode, cachedItems);
        var promotionRules = await catalogRepository.LoadPromotionRulesAsync(storeCode, cancellationToken)
            .ConfigureAwait(false);
        return new LocalCatalogReloadResult(cachedItems, promotionRules);
    }

    private void SetLoadedCatalog(string? storeCode, IReadOnlyList<SellableItemDto>? items)
    {
        lock (_syncStateGate)
        {
            _loadedCatalog = storeCode is null || items is null
                ? null
                : new LoadedCatalog(storeCode.Trim(), items);
        }
    }

    private bool TryGetLoadedCatalog(string storeCode, out IReadOnlyList<SellableItemDto> items)
    {
        lock (_syncStateGate)
        {
            if (_loadedCatalog is { } loaded &&
                string.Equals(loaded.StoreCode, storeCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                items = loaded.Items;
                return true;
            }
        }

        items = [];
        return false;
    }

    private void ApplyPromotionRules(IReadOnlyList<CatalogPromotionRuleDto> promotionRules)
    {
        // 满减规则会触发购物车事件，必须回到调用方上下文后再应用，避免后台线程更新 WPF 绑定集合。
        cart.SetAutomaticPromotionRules(promotionRules);
    }

    private sealed class CatalogSyncProgressSink(
        IProgress<CatalogSyncProgress> target,
        int compareTotalCount) : IProgress<CatalogSyncProgress>
    {
        public void Report(CatalogSyncProgress value)
        {
            target.Report(value.Stage == CatalogSyncProgressStage.Comparing
                ? value with { TotalCount = compareTotalCount }
                : value);
        }
    }

    private sealed record LocalCatalogReloadResult(
        IReadOnlyList<SellableItemDto> Items,
        IReadOnlyList<CatalogPromotionRuleDto> PromotionRules);

    private sealed record LoadedCatalog(
        string StoreCode,
        IReadOnlyList<SellableItemDto> Items);
}
