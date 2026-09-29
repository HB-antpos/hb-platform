using System.Net.Http;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Advertisements;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayOrchestratorTests
{
    [Fact]
    public void Fullscreen_request_from_window_service_is_forwarded_to_subscribers()
    {
        var windowService = new FakeCustomerDisplayWindowService();
        var orchestrator = new CustomerDisplayOrchestrator(windowService);
        var raised = 0;
        EventHandler handler = (_, _) => raised++;
        orchestrator.FullscreenRequested += handler;

        windowService.RaiseFullscreenRequested();
        orchestrator.FullscreenRequested -= handler;
        windowService.RaiseFullscreenRequested();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_loads_snapshot_from_api()
    {
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => Task.FromResult(CreateResponse(CreateImageAdvertisement("ad-1")))));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.True(customerDisplay.IsAdvertisementAvailable);
        Assert.Equal("ad-1", customerDisplay.CurrentAdvertisement?.Id);
        Assert.True(customerDisplay.IsIdleAdvertisementVisible);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_uses_cached_local_media_url()
    {
        var localMediaUrl = new Uri(Path.Combine(Path.GetTempPath(), "cached-ad-1.png")).AbsoluteUri;
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => Task.FromResult(CreateResponse(CreateImageAdvertisement("ad-1")))),
            advertisementMediaCache: new FakeAdvertisementMediaCache(items =>
                items.Select(item => item with { MediaUrl = localMediaUrl }).ToArray()));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.Equal(localMediaUrl, customerDisplay.CurrentAdvertisement?.MediaUrl);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_sends_only_currently_effective_items_to_media_cache()
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<AdvertisementPlaybackItemDto> cacheInput = [];
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => Task.FromResult(CreateResponse(
                CreateImageAdvertisement("ad-expired", now.AddMinutes(-10), now.AddMinutes(-1)),
                CreateImageAdvertisement("ad-active", now.AddMinutes(-1), now.AddMinutes(10))))),
            advertisementMediaCache: new FakeAdvertisementMediaCache(items =>
            {
                cacheInput = items;
                return items;
            }));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.Equal(["ad-active"], cacheInput.Select(item => item.Id));
        Assert.Equal("ad-active", customerDisplay.CurrentAdvertisement?.Id);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_clears_snapshot_when_all_items_are_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => Task.FromResult(CreateResponse(
                CreateImageAdvertisement("ad-expired", now.AddMinutes(-10), now.AddMinutes(-1))))),
            advertisementMediaCache: new FakeAdvertisementMediaCache(items => items));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.False(customerDisplay.IsAdvertisementAvailable);
        Assert.Null(customerDisplay.CurrentAdvertisement);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_keeps_last_snapshot_when_api_fails()
    {
        var responses = new Queue<Func<Task<AdvertisementPlaybackResponse>>>(new[]
        {
            () => Task.FromResult(CreateResponse(CreateImageAdvertisement("ad-1"))),
            () => throw new HttpRequestException("boom")
        });
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => responses.Dequeue().Invoke()));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");
        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S002");

        Assert.True(customerDisplay.IsAdvertisementAvailable);
        Assert.Equal("ad-1", customerDisplay.CurrentAdvertisement?.Id);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_first_failure_keeps_static_promotion()
    {
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => throw new HttpRequestException("boom")));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.False(customerDisplay.IsAdvertisementAvailable);
        Assert.Null(customerDisplay.CurrentAdvertisement);
        Assert.False(customerDisplay.IsIdleAdvertisementVisible);
    }

    [Fact]
    public void LoadFromCart_preserves_customer_facing_discount_values()
    {
        var orchestrator = new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService());
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-DISCOUNT", "Discounted Item", "930000000001", 10m));
        cart.AddItem(CreateItem("SKU-REGULAR", "Regular Item", "930000000002", 5m));
        cart.SetLineDiscountPercent(cart.Lines.Single(line => line.LookupCode == "930000000001"), 10m);
        var customerDisplay = new CustomerDisplayViewModel();

        orchestrator.LoadFromCart(
            customerDisplay,
            CreateSession(),
            cart,
            refreshAdvertisements: false);

        var discounted = customerDisplay.Lines.Single(line => line.LookupCode == "930000000001");
        Assert.Equal(10m, discounted.GrossAmount);
        Assert.Equal(9m, discounted.ActualAmount);
        Assert.True(discounted.HasDiscount);
        Assert.Equal("-10%", discounted.DiscountRateText);

        var regular = customerDisplay.Lines.Single(line => line.LookupCode == "930000000002");
        Assert.Equal(5m, regular.GrossAmount);
        Assert.Equal(5m, regular.ActualAmount);
        Assert.False(regular.HasDiscount);
        Assert.Empty(regular.DiscountRateText);
    }

    [Fact]
    public void LoadFromCart_projects_sale_line_item_number_image_and_lookup_code()
    {
        var orchestrator = new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService());
        var cart = new PosCartService();
        cart.AddItem(CreateItem(
            "SKU-IMAGE",
            "Image Item",
            "930000000003",
            12m,
            itemNumber: "ITEM-001",
            productImage: "/uploads/products/item-001.png"));
        var customerDisplay = new CustomerDisplayViewModel();

        orchestrator.LoadFromCart(
            customerDisplay,
            CreateSession(),
            cart,
            refreshAdvertisements: false);

        var line = Assert.Single(customerDisplay.Lines);
        Assert.Equal("ITEM-001", line.ItemNumber);
        Assert.Equal("/uploads/products/item-001.png", line.ProductImage);
        Assert.Equal("930000000003", line.LookupCode);
    }

    [Fact]
    public void LoadFromCart_projects_return_line_item_number_image_and_lookup_code()
    {
        var orchestrator = new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService());
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            StoreCode: "S001",
            ProductCode: "SKU-RETURN",
            ReferenceCode: "REF-RETURN",
            DisplayName: "Returned Item",
            LookupCode: "930000000004",
            ItemNumber: "ITEM-RETURN-001",
            ProductImage: "https://cdn.example.com/products/return-item.png",
            Quantity: 1m,
            UnitPrice: 8m,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "StoreRetailPrice",
            ReturnSourceKey: "return-line-001",
            OriginalOrderGuid: Guid.NewGuid(),
            OriginalOrderLineGuid: Guid.NewGuid()));
        var customerDisplay = new CustomerDisplayViewModel();

        orchestrator.LoadFromCart(
            customerDisplay,
            CreateSession(),
            cart,
            refreshAdvertisements: false);

        var line = Assert.Single(customerDisplay.Lines);
        Assert.Equal("ITEM-RETURN-001", line.ItemNumber);
        Assert.Equal("https://cdn.example.com/products/return-item.png", line.ProductImage);
        Assert.Equal("930000000004", line.LookupCode);
    }

    [Fact]
    public async Task LoadFromCart_starts_periodic_advertisement_refresh()
    {
        var callCount = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ =>
            {
                if (Interlocked.Increment(ref callCount) >= 2)
                {
                    secondRefresh.TrySetResult();
                }

                return Task.FromResult(CreateResponse(CreateImageAdvertisement($"ad-{callCount}")));
            }),
            TimeSpan.FromMilliseconds(25));
        var customerDisplay = new CustomerDisplayViewModel();

        orchestrator.LoadFromCart(customerDisplay, CreateSession(), new PosCartService());

        await secondRefresh.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task LoadFromCart_default_refresh_keeps_advertisement_interval()
    {
        var callCount = 0;
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ =>
            {
                var currentCallCount = Interlocked.Increment(ref callCount);
                return Task.FromResult(CreateResponse(CreateImageAdvertisement($"ad-{currentCallCount}")));
            }),
            TimeSpan.FromHours(1));
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();

        orchestrator.LoadFromCart(customerDisplay, session, new PosCartService());
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), 1);

        orchestrator.LoadFromCart(customerDisplay, session, new PosCartService());
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task LoadFromCart_forceAdvertisementRefresh_bypasses_advertisement_interval()
    {
        var callCount = 0;
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ =>
            {
                var currentCallCount = Interlocked.Increment(ref callCount);
                return Task.FromResult(CreateResponse(CreateImageAdvertisement($"ad-{currentCallCount}")));
            }),
            TimeSpan.FromHours(1));
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();

        orchestrator.LoadFromCart(customerDisplay, session, new PosCartService(), forceAdvertisementRefresh: true);
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), 1);

        orchestrator.LoadFromCart(customerDisplay, session, new PosCartService(), forceAdvertisementRefresh: true);
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), 2);

        Assert.Equal("ad-2", customerDisplay.CurrentAdvertisement?.Id);
    }

    [Fact]
    public async Task SetMode_open_from_closed_forces_advertisement_refresh()
    {
        var callCount = 0;
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ =>
            {
                var currentCallCount = Interlocked.Increment(ref callCount);
                return Task.FromResult(CreateResponse(CreateImageAdvertisement($"ad-{currentCallCount}")));
            }),
            TimeSpan.FromHours(1));
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();
        var cart = new PosCartService();

        orchestrator.LoadFromCart(customerDisplay, session, cart, forceAdvertisementRefresh: true);
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), 1);

        orchestrator.SetMode(CustomerDisplayWindowMode.Fullscreen, customerDisplay, session, cart, owner: null);
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), 2);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task SetMode_size_switch_and_close_do_not_force_advertisement_refresh()
    {
        var callCount = 0;
        var windowService = new FakeCustomerDisplayWindowService
        {
            Mode = CustomerDisplayWindowMode.Normal
        };
        var orchestrator = new CustomerDisplayOrchestrator(
            windowService,
            new FakeAdvertisementApiClient(_ =>
            {
                var currentCallCount = Interlocked.Increment(ref callCount);
                return Task.FromResult(CreateResponse(CreateImageAdvertisement($"ad-{currentCallCount}")));
            }),
            TimeSpan.FromMilliseconds(25));
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();
        var cart = new PosCartService();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, session.StoreCode);
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), 1);
        await Task.Delay(75);

        orchestrator.SetMode(CustomerDisplayWindowMode.Fullscreen, customerDisplay, session, cart, owner: null);
        orchestrator.SetMode(CustomerDisplayWindowMode.Closed, customerDisplay, session, cart, owner: null);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref callCount));
    }

    // 复用共享等待：原私有副本只有 2 秒预算，超时时只抛无上下文的 TaskCanceledException。
    private static Task WaitForCallCountAsync(Func<int> getCallCount, int expectedCallCount) =>
        WaitUntilAsync(
            () => getCallCount() >= expectedCallCount,
            diagnostics: () => $"callCount={getCallCount()} expected>={expectedCallCount}");

    private static AdvertisementPlaybackResponse CreateResponse(params AdvertisementPlaybackItemDto[] items)
    {
        return new AdvertisementPlaybackResponse("S001", DateTimeOffset.UtcNow, items);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            SystemName: "HB POS",
            StoreCode: "S001",
            StoreName: "Main Store",
            DeviceCode: "POS-01",
            CashierId: "C001",
            CashierName: "Alice",
            IsOnline: true,
            PendingSyncCount: 0);
    }

    private static SellableItemDto CreateItem(
        string productCode,
        string displayName,
        string lookupCode,
        decimal price,
        string? itemNumber = null,
        string? productImage = null)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "StoreRetailPrice",
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }

    private static AdvertisementPlaybackItemDto CreateImageAdvertisement(string id)
    {
        return CreateImageAdvertisement(
            id,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static AdvertisementPlaybackItemDto CreateImageAdvertisement(
        string id,
        DateTimeOffset effectiveStart,
        DateTimeOffset effectiveEnd)
    {
        return new AdvertisementPlaybackItemDto(
            id,
            $"Ad {id}",
            $"Description {id}",
            "image",
            $"https://cdn.example.com/{id}.png",
            null,
            $"object/{id}",
            $"{id}.png",
            "image/png",
            1024,
            effectiveStart,
            effectiveEnd,
            1);
    }

    private sealed class FakeAdvertisementApiClient(
        Func<string, Task<AdvertisementPlaybackResponse>> handler) : IAdvertisementApiClient
    {
        public Task<AdvertisementPlaybackResponse> GetActiveAsync(
            string storeCode,
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            return handler(storeCode);
        }
    }

    private sealed class FakeAdvertisementMediaCache(
        Func<IReadOnlyList<AdvertisementPlaybackItemDto>, IReadOnlyList<AdvertisementPlaybackItemDto>> handler) : IAdvertisementMediaCache
    {
        public Task<IReadOnlyList<AdvertisementPlaybackItemDto>> CacheAsync(
            IReadOnlyList<AdvertisementPlaybackItemDto> advertisements,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(handler(advertisements));
        }
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public bool IsOpen => false;

        public CustomerDisplayWindowMode Mode { get; set; } = CustomerDisplayWindowMode.Closed;

        public event EventHandler? Closed
        {
            add { }
            remove { }
        }

        public event EventHandler? FullscreenRequested;

        public void RaiseFullscreenRequested() => FullscreenRequested?.Invoke(this, EventArgs.Empty);

        public void Prewarm(CustomerDisplayViewModel viewModel)
        {
        }

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, System.Windows.Window? owner)
        {
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, null);
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, System.Windows.Window? owner)
        {
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, null);
        }

        public CustomerDisplayWindowResult SetMode(
            CustomerDisplayWindowMode mode,
            CustomerDisplayViewModel viewModel,
            System.Windows.Window? owner)
        {
            Mode = mode;
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, null);
        }
    }
}
