using System.Diagnostics;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 启动目录加载协调器：承接启动阶段的目录索引加载、超时取消、初始同步和特殊商品数据预加载。
/// MainViewModel.InitializeAsync 保留为入口编排方法，不改变设备注册优先级、preview mode、离线可用逻辑。
/// </summary>
internal sealed class CatalogStartupCoordinator
{
    private static readonly TimeSpan StartupCatalogIndexLoadTimeout = TimeSpan.FromSeconds(30);

    private readonly IShellCatalogService _shellCatalogService;
    private readonly ILocalCatalogRepository _catalogRepository;
    private readonly ILocalizationService _localization;
    private readonly Action<string> _setStatusMessage;

    private CancellationTokenSource? _startupCatalogIndexLoadCts;
    private Task<IReadOnlyList<SellableItemDto>>? _startupCatalogIndexLoadTask;

    public CatalogStartupCoordinator(
        IShellCatalogService shellCatalogService,
        ILocalCatalogRepository catalogRepository,
        ILocalizationService localization,
        Action<string> setStatusMessage)
    {
        _shellCatalogService = shellCatalogService;
        _catalogRepository = catalogRepository;
        _localization = localization;
        _setStatusMessage = setStatusMessage;
    }

    /// <summary>
    /// 启动阶段加载本地目录索引（带超时和去重保护）。preview 模式跳过。
    /// </summary>
    public async Task<IReadOnlyList<SellableItemDto>> LoadStartupCatalogIndexAsync(
        string storeCode,
        bool isPreviewMode,
        Action<IReadOnlyList<SellableItemDto>>? onLoaded = null,
        Action? onCartRefresh = null)
    {
        if (isPreviewMode)
        {
            return [];
        }

        _startupCatalogIndexLoadCts ??= new CancellationTokenSource();
        _startupCatalogIndexLoadCts.CancelAfter(StartupCatalogIndexLoadTimeout);
        _startupCatalogIndexLoadTask ??= LoadLocalCatalogCoreAsync(storeCode, _startupCatalogIndexLoadCts.Token, onLoaded, onCartRefresh, hasStartupTimeout: true);
        var cts = _startupCatalogIndexLoadCts;
        var loadTask = _startupCatalogIndexLoadTask;
        try
        {
            return await loadTask;
        }
        finally
        {
            if (ReferenceEquals(_startupCatalogIndexLoadCts, cts))
            {
                _startupCatalogIndexLoadCts = null;
            }

            if (ReferenceEquals(_startupCatalogIndexLoadTask, loadTask))
            {
                _startupCatalogIndexLoadTask = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 直接加载本地目录（绕过超时/单次调用保护，用于后台同步触发的 LoadMatches）。
    /// </summary>
    public async Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogForStartupAsync(
        string storeCode,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<SellableItemDto>>? onLoaded = null,
        Action? onCartRefresh = null)
        => await LoadLocalCatalogCoreAsync(storeCode, cancellationToken, onLoaded, onCartRefresh, hasStartupTimeout: false);

    private async Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogCoreAsync(
        string storeCode,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<SellableItemDto>>? onLoaded,
        Action? onCartRefresh,
        bool hasStartupTimeout)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write("CatalogStartup", $"local catalog load start store={storeCode}");
        try
        {
            var cachedItems = await _shellCatalogService.LoadLocalCatalogAsync(storeCode, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            onLoaded?.Invoke(cachedItems);
            onCartRefresh?.Invoke();
            stopwatch.Stop();
            ConsoleLog.Write("CatalogStartup", $"local catalog load completed store={storeCode} items={cachedItems.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return cachedItems;
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            // 启动加载与 CancelStartupLoad 共用同一个 CTS（CancelAfter 30 秒），只能按耗时区分：
            // 启动路径耗时已接近 30 秒视为超时（记 Warning），否则是调用方取消（保持 Information）。
            if (hasStartupTimeout && stopwatch.Elapsed >= StartupCatalogIndexLoadTimeout - TimeSpan.FromSeconds(1))
            {
                ConsoleLog.WriteWarning(
                    "CatalogStartup",
                    $"local catalog load timed out store={storeCode} elapsedMs={stopwatch.ElapsedMilliseconds} timeoutSeconds={StartupCatalogIndexLoadTimeout.TotalSeconds:0}",
                    new ApplicationLogContext(Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["storeCode"] = storeCode,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                        ["reason"] = "timeout"
                    }),
                    ex);
            }
            else
            {
                ConsoleLog.Write("CatalogStartup", $"local catalog load canceled store={storeCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            return [];
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.WriteError(
                "CatalogStartup",
                $"local catalog load failed store={storeCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}",
                new ApplicationLogContext(Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["storeCode"] = storeCode,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                }),
                ex);
            _setStatusMessage(ex.Message);
            return [];
        }
    }

    /// <summary>
    /// 取消正在进行的启动目录索引加载。
    /// </summary>
    public void CancelStartupLoad()
    {
        var cts = _startupCatalogIndexLoadCts;
        _startupCatalogIndexLoadCts = null;
        _startupCatalogIndexLoadTask = null;
        cts?.Cancel();
    }

    /// <summary>
    /// 检查本地是否有目录数据。
    /// </summary>
    public async Task<bool> HasLocalCatalogItemsAsync(string storeCode)
    {
        try
        {
            var firstPage = await _catalogRepository.LoadSellableItemComparePageAsync(
                storeCode,
                afterLookupCodeNormalized: null,
                pageSize: 1);
            return firstPage.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.Write("CatalogSync", $"initial sync local cache probe failed store={storeCode} error={ex.Message}");
            return true;
        }
    }
}
