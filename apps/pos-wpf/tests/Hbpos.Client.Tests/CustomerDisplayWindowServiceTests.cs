using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayWindowServiceTests
{
    private static readonly DisplayBounds TargetDisplay = new(
        IntPtr.Zero,
        MonitorLeft: 100,
        MonitorTop: 200,
        MonitorWidth: 1920,
        MonitorHeight: 1080,
        WorkAreaLeft: 140,
        WorkAreaTop: 240,
        WorkAreaWidth: 1000,
        WorkAreaHeight: 700);

    [Fact]
    public void Fullscreen_layout_plan_uses_full_bounds_normal_state_and_topmost()
    {
        var plan = CustomerDisplayWindowService.GetLayoutPlan(CustomerDisplayWindowMode.Fullscreen);

        Assert.True(plan.TitleBarVisibleDuringPlacement);
        Assert.False(plan.CenterAfterPlacement);
        Assert.True(plan.UseFullDisplayBoundsForPlacement);
        Assert.Equal(WindowState.Normal, plan.FinalWindowState);
        Assert.False(plan.TitleBarVisibleAfterStateChange);
        Assert.True(ReadTopmost(plan));
    }

    [Fact]
    public void Normal_layout_plan_keeps_titlebar_visible_and_centered()
    {
        var plan = CustomerDisplayWindowService.GetLayoutPlan(CustomerDisplayWindowMode.Normal);

        Assert.True(plan.TitleBarVisibleDuringPlacement);
        Assert.True(plan.CenterAfterPlacement);
        Assert.False(plan.UseFullDisplayBoundsForPlacement);
        Assert.Equal(WindowState.Normal, plan.FinalWindowState);
        Assert.True(plan.TitleBarVisibleAfterStateChange);
        Assert.False(ReadTopmost(plan));
    }

    [Theory]
    [InlineData(CustomerDisplayWindowMode.Normal, true)]
    [InlineData(CustomerDisplayWindowMode.Fullscreen, false)]
    [InlineData(CustomerDisplayWindowMode.Closed, false)]
    public void Fullscreen_request_is_forwarded_only_from_the_titled_normal_window(
        CustomerDisplayWindowMode mode,
        bool expected)
    {
        Assert.Equal(expected, CustomerDisplayWindowService.ShouldForwardFullscreenRequest(mode));
    }

    [Fact]
    public void Late_fullscreen_request_after_close_is_ignored()
    {
        var service = new CustomerDisplayWindowService(new DeterministicDisplayTopologyService());
        var raised = 0;
        service.FullscreenRequested += (_, _) => raised++;

        service.OnFullscreenRequested();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Title_bar_double_click_requests_fullscreen_instead_of_maximizing_to_work_area()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Windows", "CustomerDisplayWindow.xaml.cs"));

        Assert.Contains("FullscreenRequested?.Invoke(this, EventArgs.Empty);", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowState.Maximized", codeBehind, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }

    private static bool ReadTopmost(object plan)
    {
        var property = plan.GetType().GetProperty("Topmost");
        Assert.NotNull(property);
        return Assert.IsType<bool>(property.GetValue(plan));
    }

    [Fact]
    public Task ApplyMode_fullscreen_then_normal_restores_window_state_and_uses_expected_bounds()
    {
        return RunOnStaDispatcherAsync(() =>
        {
            var service = new CustomerDisplayWindowService(new DeterministicDisplayTopologyService());
            var window = new Window
            {
                Width = 1024,
                Height = 640,
                MinWidth = 800,
                MinHeight = 520,
                ResizeMode = ResizeMode.CanResize,
                WindowState = WindowState.Normal
            };
            var owner = new Window();
            var titleBar = new Border();

            void SetTitleBarVisible(bool isVisible)
            {
                titleBar.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                window.ResizeMode = isVisible ? ResizeMode.CanResize : ResizeMode.NoResize;
            }

            service.ApplyModeCore(
                window,
                owner,
                TargetDisplay,
                CustomerDisplayWindowMode.Fullscreen,
                showWindow: static () => { },
                setTitleBarVisible: SetTitleBarVisible,
                refreshContentLayout: static () => { });

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(window.Topmost);
            Assert.Equal(Visibility.Collapsed, titleBar.Visibility);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.Equal(100d, window.Left);
            Assert.Equal(200d, window.Top);
            Assert.Equal(1920d, window.Width);
            Assert.Equal(1080d, window.Height);

            service.ApplyModeCore(
                window,
                owner,
                TargetDisplay,
                CustomerDisplayWindowMode.Normal,
                showWindow: static () => { },
                setTitleBarVisible: SetTitleBarVisible,
                refreshContentLayout: static () => { });

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.False(window.Topmost);
            Assert.Equal(Visibility.Visible, titleBar.Visibility);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            Assert.Equal(240d, window.Left);
            Assert.Equal(317d, window.Top);
            Assert.Equal(800d, window.Width);
            Assert.Equal(546d, window.Height);
        });
    }

    private static async Task RunOnStaDispatcherAsync(Action action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.CustomerDisplayWindowDispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }

    [Fact]
    public void Prewarm_loads_cart_into_view_model_and_calls_window_service_once()
    {
        var windowService = new FakeCustomerDisplayWindowService();
        var orchestrator = new CustomerDisplayOrchestrator(windowService, new FakeAdvertisementApiClient());
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPLE", "Apple", "PLU001", 3.50m));
        cart.AddItem(CreateItem("SKU-APPLE", "Apple", "PLU001", 3.50m));

        orchestrator.Prewarm(customerDisplay, session, cart);

        Assert.Equal(1, windowService.PrewarmCallCount);
        Assert.Same(customerDisplay, windowService.LastPrewarmedViewModel);
        Assert.Equal("POS-1001", customerDisplay.TerminalName);
        Assert.Single(customerDisplay.Lines);
        Assert.Equal("Apple", customerDisplay.Lines[0].DisplayName);
        Assert.Equal("PLU001", customerDisplay.Lines[0].LookupCode);
        Assert.Equal(2, customerDisplay.TotalItemQuantity);
        Assert.Equal(7.00m, customerDisplay.TotalToPay);
    }

    [Fact]
    public void SetMode_after_prewarm_preserves_no_second_display_result()
    {
        var expected = new CustomerDisplayWindowResult(
            CustomerDisplayWindowMode.Closed,
            CustomerDisplayWindowService.NoSecondDisplayStatusKey);
        var windowService = new FakeCustomerDisplayWindowService
        {
            NextSetModeResult = expected
        };
        var orchestrator = new CustomerDisplayOrchestrator(windowService, new FakeAdvertisementApiClient());
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPLE", "Apple", "PLU001", 4.20m));

        orchestrator.Prewarm(customerDisplay, session, cart);
        var result = orchestrator.SetMode(
            CustomerDisplayWindowMode.Fullscreen,
            customerDisplay,
            session,
            cart,
            owner: null);

        Assert.Equal(1, windowService.PrewarmCallCount);
        Assert.Equal(1, windowService.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, windowService.LastRequestedMode);
        Assert.Equal(expected, result);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            SystemName: "HB POS",
            StoreCode: "S001",
            StoreName: "Main Store",
            DeviceCode: "POS-1001",
            CashierId: "C001",
            CashierName: "Alice",
            IsOnline: false,
            PendingSyncCount: 0);
    }

    private static SellableItemDto CreateItem(string productCode, string displayName, string lookupCode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "StoreRetailPrice",
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: null);
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public bool IsOpen => Mode != CustomerDisplayWindowMode.Closed;

        public CustomerDisplayWindowMode Mode { get; private set; }

        public int PrewarmCallCount { get; private set; }

        public int SetModeCallCount { get; private set; }

        public CustomerDisplayViewModel? LastPrewarmedViewModel { get; private set; }

        public CustomerDisplayWindowMode LastRequestedMode { get; private set; }

        public CustomerDisplayWindowResult NextSetModeResult { get; init; } = new(
            CustomerDisplayWindowMode.Fullscreen,
            CustomerDisplayWindowService.OpenedFullscreenStatusKey);

        public event EventHandler? Closed
        {
            add { }
            remove { }
        }

        public void Prewarm(CustomerDisplayViewModel viewModel)
        {
            PrewarmCallCount++;
            LastPrewarmedViewModel = viewModel;
        }

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
        {
            return SetMode(CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
        {
            var nextMode = Mode == CustomerDisplayWindowMode.Closed
                ? CustomerDisplayWindowMode.Fullscreen
                : CustomerDisplayWindowMode.Closed;
            return SetMode(nextMode, viewModel, owner);
        }

        public CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner)
        {
            SetModeCallCount++;
            LastRequestedMode = mode;
            Mode = NextSetModeResult.Mode;
            return NextSetModeResult;
        }
    }

    private sealed class FakeAdvertisementApiClient : IAdvertisementApiClient
    {
        public Task<Hbpos.Contracts.Advertisements.AdvertisementPlaybackResponse> GetActiveAsync(
            string storeCode,
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Hbpos.Contracts.Advertisements.AdvertisementPlaybackResponse(
                storeCode,
                DateTimeOffset.UtcNow,
                []));
        }
    }

    private sealed class DeterministicDisplayTopologyService : IDisplayTopologyService
    {
        public IReadOnlyList<DisplayBounds> GetDisplays()
        {
            return [TargetDisplay];
        }

        public DisplayBounds? FindDisplayAwayFrom(Window owner)
        {
            return TargetDisplay;
        }

        public void AttachWorkAreaConstraint(Window window)
        {
        }

        public void FitToDisplayWorkArea(Window window, DisplayBounds display)
        {
            ApplyBounds(
                window,
                display.WorkAreaLeft,
                display.WorkAreaTop,
                display.WorkAreaWidth,
                display.WorkAreaHeight);
        }

        public void FitToDisplayBounds(Window window, DisplayBounds display)
        {
            ApplyBounds(
                window,
                display.MonitorLeft,
                display.MonitorTop,
                display.MonitorWidth,
                display.MonitorHeight);
        }

        private static void ApplyBounds(Window window, int left, int top, int width, int height)
        {
            window.Left = left;
            window.Top = top;
            window.Width = Math.Max(window.MinWidth, width);
            window.Height = Math.Max(window.MinHeight, height);
            window.MaxWidth = window.Width;
            window.MaxHeight = window.Height;
        }
    }
}
