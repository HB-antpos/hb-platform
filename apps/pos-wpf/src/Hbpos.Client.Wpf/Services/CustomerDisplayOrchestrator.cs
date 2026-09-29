using System.Diagnostics;
using System.Windows;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Wpf.Services;

public interface ICustomerDisplayOrchestrator
{
    event EventHandler? Closed;

    /// <summary>客显窗口请求切到全屏（普通模式下双击标题栏）。</summary>
    event EventHandler? FullscreenRequested
    {
        add { }
        remove { }
    }

    void LoadFromCart(
        CustomerDisplayViewModel customerDisplay,
        PosSessionState session,
        PosCartService cart,
        bool forceAdvertisementRefresh = false,
        bool refreshAdvertisements = true);

    void Prewarm(CustomerDisplayViewModel customerDisplay, PosSessionState session, PosCartService cart)
    {
    }

    CustomerDisplayWindowMode GetNextMode(CustomerDisplayWindowMode currentMode);

    CustomerDisplayWindowResult SetMode(
        CustomerDisplayWindowMode mode,
        CustomerDisplayViewModel customerDisplay,
        PosSessionState session,
        PosCartService cart,
        Window? owner);
}

public sealed class CustomerDisplayOrchestrator : ICustomerDisplayOrchestrator
{
    private const int AdvertisementTake = 20;
    private static readonly TimeSpan DefaultAdvertisementRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly ICustomerDisplayWindowService customerDisplayWindowService;
    private readonly IAdvertisementApiClient advertisementApiClient;
    private readonly IAdvertisementMediaCache advertisementMediaCache;
    private readonly TimeSpan advertisementRefreshInterval;
    private readonly SemaphoreSlim _advertisementRefreshGate = new(1, 1);
    private IReadOnlyList<AdvertisementPlaybackItemDto> _cachedAdvertisements = [];
    private bool _hasAdvertisementSnapshot;
    private DateTimeOffset _lastAdvertisementRefreshUtc = DateTimeOffset.MinValue;
    private string? _lastAdvertisementStoreCode;
    private CancellationTokenSource? _periodicRefreshCts;
    private CustomerDisplayViewModel? _periodicRefreshCustomerDisplay;
    private string? _periodicRefreshStoreCode;

    public CustomerDisplayOrchestrator(ICustomerDisplayWindowService customerDisplayWindowService)
        : this(customerDisplayWindowService, NullAdvertisementApiClient.Instance)
    {
    }

    public CustomerDisplayOrchestrator(
        ICustomerDisplayWindowService customerDisplayWindowService,
        IAdvertisementApiClient advertisementApiClient,
        IAdvertisementMediaCache? advertisementMediaCache = null)
        : this(customerDisplayWindowService, advertisementApiClient, DefaultAdvertisementRefreshInterval, advertisementMediaCache)
    {
    }

    internal CustomerDisplayOrchestrator(
        ICustomerDisplayWindowService customerDisplayWindowService,
        IAdvertisementApiClient advertisementApiClient,
        TimeSpan advertisementRefreshInterval,
        IAdvertisementMediaCache? advertisementMediaCache = null)
    {
        this.customerDisplayWindowService = customerDisplayWindowService;
        this.advertisementApiClient = advertisementApiClient;
        this.advertisementMediaCache = advertisementMediaCache ?? NullAdvertisementMediaCache.Instance;
        this.advertisementRefreshInterval = advertisementRefreshInterval <= TimeSpan.Zero
            ? DefaultAdvertisementRefreshInterval
            : advertisementRefreshInterval;
        customerDisplayWindowService.Closed += (_, _) => StopAdvertisementRefresh();
    }

    public event EventHandler? Closed
    {
        add => customerDisplayWindowService.Closed += value;
        remove => customerDisplayWindowService.Closed -= value;
    }

    public event EventHandler? FullscreenRequested
    {
        add => customerDisplayWindowService.FullscreenRequested += value;
        remove => customerDisplayWindowService.FullscreenRequested -= value;
    }

    public void LoadFromCart(
        CustomerDisplayViewModel customerDisplay,
        PosSessionState session,
        PosCartService cart,
        bool forceAdvertisementRefresh = false,
        bool refreshAdvertisements = true)
    {
        var lines = cart.Lines.Select(line => new CustomerDisplayLine(
            line.DisplayName,
            line.LookupCode,
            line.Quantity,
            line.UnitPrice,
            line.ActualAmount)
        {
            ItemNumber = line.ItemNumber,
            ProductImage = line.ProductImage,
            GrossAmount = line.GrossAmount,
            HasDiscount = line.HasDiscount,
            DiscountRateText = line.DiscountRateText
        });
        customerDisplay.TerminalName = session.DeviceCode;
        customerDisplay.LoadLines(lines, cart.TotalAmount, cart.DiscountAmount);
        if (!refreshAdvertisements)
        {
            return;
        }

        StartAdvertisementRefresh(customerDisplay, session.StoreCode, forceAdvertisementRefresh);
    }

    public void Prewarm(CustomerDisplayViewModel customerDisplay, PosSessionState session, PosCartService cart)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"prewarm start store={session.StoreCode} device={session.DeviceCode} lines={cart.Lines.Count} windowMode={customerDisplayWindowService.Mode}");
        try
        {
            LoadFromCart(customerDisplay, session, cart);
            customerDisplayWindowService.Prewarm(customerDisplay);
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"prewarm completed store={session.StoreCode} device={session.DeviceCode} isOpen={customerDisplayWindowService.IsOpen} windowMode={customerDisplayWindowService.Mode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"prewarm failed store={session.StoreCode} device={session.DeviceCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public CustomerDisplayWindowMode GetNextMode(CustomerDisplayWindowMode currentMode)
    {
        return currentMode switch
        {
            CustomerDisplayWindowMode.Closed => CustomerDisplayWindowMode.Normal,
            CustomerDisplayWindowMode.Normal => CustomerDisplayWindowMode.Fullscreen,
            _ => CustomerDisplayWindowMode.Closed
        };
    }

    public CustomerDisplayWindowResult SetMode(
        CustomerDisplayWindowMode mode,
        CustomerDisplayViewModel customerDisplay,
        PosSessionState session,
        PosCartService cart,
        Window? owner)
    {
        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"set-mode start requestedMode={mode} store={session.StoreCode} device={session.DeviceCode} ownerPresent={owner is not null} currentMode={customerDisplayWindowService.Mode}");
        var forceAdvertisementRefresh = customerDisplayWindowService.Mode == CustomerDisplayWindowMode.Closed
            && mode != CustomerDisplayWindowMode.Closed;
        if (mode == CustomerDisplayWindowMode.Closed)
        {
            StopAdvertisementRefresh();
        }
        else
        {
            // 只有从关闭状态打开客屏时触发广告刷新；大小切换只同步购物车内容。
            LoadFromCart(
                customerDisplay,
                session,
                cart,
                forceAdvertisementRefresh,
                refreshAdvertisements: forceAdvertisementRefresh);
        }

        var result = customerDisplayWindowService.SetMode(mode, customerDisplay, owner);
        stopwatch.Stop();
        ConsoleLog.Write(
            "CustomerDisplay",
            $"set-mode completed requestedMode={mode} resultMode={result.Mode} statusKey={result.StatusMessageKey ?? "<none>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return result;
    }

    internal async Task RefreshAdvertisementsAsync(
        CustomerDisplayViewModel customerDisplay,
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        await RefreshAdvertisementsAsync(customerDisplay, storeCode, force: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RefreshAdvertisementsAsync(
        CustomerDisplayViewModel customerDisplay,
        string storeCode,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode))
        {
            return;
        }

        if (!force && !ShouldRefreshAdvertisements(normalizedStoreCode))
        {
            return;
        }

        if (!await _advertisementRefreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        ConsoleLog.Write("CustomerDisplay", $"advertisement refresh start store={normalizedStoreCode} force={force}");
        try
        {
            if (!force && !ShouldRefreshAdvertisements(normalizedStoreCode))
            {
                return;
            }

            AdvertisementPlaybackResponse response;
            try
            {
                response = await advertisementApiClient
                    .GetActiveAsync(normalizedStoreCode, AdvertisementTake, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                ConsoleLog.Write("CustomerDisplay", $"advertisement refresh canceled store={normalizedStoreCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ConsoleLog.Write(
                    "CustomerDisplay",
                    $"advertisement refresh failed store={normalizedStoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<AdvertisementPlaybackItemDto> advertisements = response.Items
                .Where(advertisement => IsCurrentlyEffective(advertisement, now))
                .ToArray();
            try
            {
                // 客显图片/视频可能较大，先落本地缓存再交给 WPF 播放控件，降低远程流式播放卡顿。
                advertisements = await advertisementMediaCache
                    .CacheAsync(advertisements, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ConsoleLog.Write(
                    "CustomerDisplay",
                    $"advertisement media cache failed store={normalizedStoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            }

            _cachedAdvertisements = advertisements;
            _hasAdvertisementSnapshot = true;
            _lastAdvertisementStoreCode = normalizedStoreCode;
            _lastAdvertisementRefreshUtc = DateTimeOffset.UtcNow;

            await ApplyAdvertisementSnapshotAsync(customerDisplay, _cachedAdvertisements).ConfigureAwait(false);
            stopwatch.Stop();
            ConsoleLog.Write(
                "CustomerDisplay",
                $"advertisement refresh completed store={normalizedStoreCode} items={_cachedAdvertisements.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        finally
        {
            _advertisementRefreshGate.Release();
        }
    }

    private void StartAdvertisementRefresh(
        CustomerDisplayViewModel customerDisplay,
        string storeCode,
        bool forceAdvertisementRefresh)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode))
        {
            StopAdvertisementRefresh();
            return;
        }

        _ = RefreshAdvertisementsAsync(
            customerDisplay,
            normalizedStoreCode,
            force: forceAdvertisementRefresh);

        if (_periodicRefreshCts is not null
            && !_periodicRefreshCts.IsCancellationRequested
            && ReferenceEquals(_periodicRefreshCustomerDisplay, customerDisplay)
            && string.Equals(_periodicRefreshStoreCode, normalizedStoreCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopAdvertisementRefresh();
        var cts = new CancellationTokenSource();
        _periodicRefreshCts = cts;
        _periodicRefreshCustomerDisplay = customerDisplay;
        _periodicRefreshStoreCode = normalizedStoreCode;
        ConsoleLog.Write(
            "CustomerDisplay",
            $"advertisement periodic refresh started store={normalizedStoreCode} intervalSeconds={advertisementRefreshInterval.TotalSeconds:0}");
        _ = RunPeriodicAdvertisementRefreshAsync(customerDisplay, normalizedStoreCode, cts.Token);
    }

    private async Task RunPeriodicAdvertisementRefreshAsync(
        CustomerDisplayViewModel customerDisplay,
        string storeCode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(advertisementRefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAdvertisementsAsync(customerDisplay, storeCode, force: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StopAdvertisementRefresh()
    {
        var cts = _periodicRefreshCts;
        _periodicRefreshCts = null;
        _periodicRefreshCustomerDisplay = null;
        _periodicRefreshStoreCode = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
        ConsoleLog.Write("CustomerDisplay", "advertisement periodic refresh stopped");
    }

    private bool ShouldRefreshAdvertisements(string storeCode)
    {
        if (!_hasAdvertisementSnapshot)
        {
            return true;
        }

        if (!string.Equals(_lastAdvertisementStoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DateTimeOffset.UtcNow - _lastAdvertisementRefreshUtc >= advertisementRefreshInterval;
    }

    private static async Task ApplyAdvertisementSnapshotAsync(
        CustomerDisplayViewModel customerDisplay,
        IReadOnlyList<AdvertisementPlaybackItemDto> advertisements)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            customerDisplay.LoadAdvertisements(advertisements);
            return;
        }

        await dispatcher.InvokeAsync(() => customerDisplay.LoadAdvertisements(advertisements));
    }

    private static string NormalizeStoreCode(string? storeCode)
    {
        return (storeCode ?? string.Empty).Trim();
    }

    private static bool IsCurrentlyEffective(AdvertisementPlaybackItemDto advertisement, DateTimeOffset now)
    {
        return advertisement.EffectiveStart <= now && advertisement.EffectiveEnd >= now;
    }

    private sealed class NullAdvertisementApiClient : IAdvertisementApiClient
    {
        public static NullAdvertisementApiClient Instance { get; } = new();

        public Task<AdvertisementPlaybackResponse> GetActiveAsync(
            string storeCode,
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AdvertisementPlaybackResponse(
                NormalizeStoreCode(storeCode),
                DateTimeOffset.UtcNow,
                []));
        }
    }

    private sealed class NullAdvertisementMediaCache : IAdvertisementMediaCache
    {
        public static NullAdvertisementMediaCache Instance { get; } = new();

        public Task<IReadOnlyList<AdvertisementPlaybackItemDto>> CacheAsync(
            IReadOnlyList<AdvertisementPlaybackItemDto> advertisements,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(advertisements);
        }
    }
}
