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

    [Theory]
    [InlineData(CustomerDisplayWindowMode.Normal, true)]
    [InlineData(CustomerDisplayWindowMode.Fullscreen, true)]
    [InlineData(CustomerDisplayWindowMode.Closed, false)]
    public void Swap_screens_request_is_forwarded_in_normal_and_fullscreen(CustomerDisplayWindowMode mode, bool expected)
    {
        Assert.Equal(expected, CustomerDisplayWindowService.ShouldForwardSwapScreensRequest(mode));
    }

    [Fact]
    public void Late_swap_screens_request_after_close_is_ignored()
    {
        var service = new CustomerDisplayWindowService(new DeterministicDisplayTopologyService());
        var raised = 0;
        service.SwapScreensRequested += (_, _) => raised++;

        service.OnSwapScreensRequested();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Swap_screens_button_lives_in_customer_display_window_not_main_window()
    {
        var wpfRoot = Path.Combine(FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf");
        var customerXaml = File.ReadAllText(Path.Combine(wpfRoot, "Views", "Windows", "CustomerDisplayWindow.xaml"));
        var customerCodeBehind = File.ReadAllText(Path.Combine(wpfRoot, "Views", "Windows", "CustomerDisplayWindow.xaml.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(wpfRoot, "MainWindow.xaml"));

        // 窗口模式在标题栏、全屏在右上角常显，两处共用同一个点击处理。
        Assert.Equal(2, customerXaml.Split("Click=\"SwapScreensButton_Click\"").Length - 1);
        Assert.Contains("x:Name=\"FullscreenSwapScreensButton\"", customerXaml, StringComparison.Ordinal);
        Assert.Contains(
            "FullscreenSwapScreensButton.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;",
            customerCodeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SwapCustomerDisplayScreensCommand", mainXaml, StringComparison.Ordinal);
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

    [Theory]
    // 上次位置完整落在目标显示器工作区内：原样恢复。
    [InlineData(300d, 360d, 820d, 560d, 300d, 360d, 820d, 560d)]
    // 上次窗口有一部分拖出了工作区底部：尺寸不变，位置夹回屏内。
    [InlineData(300d, 400d, 820d, 560d, 300d, 380d, 820d, 560d)]
    // 上次窗口在另一块显示器上（与工作区无交集）：回落默认居中。
    [InlineData(3000d, 0d, 900d, 600d, 240d, 317d, 800d, 546d)]
    public Task ApplyMode_normal_restores_remembered_bounds_inside_target_work_area(
        double savedLeft,
        double savedTop,
        double savedWidth,
        double savedHeight,
        double expectedLeft,
        double expectedTop,
        double expectedWidth,
        double expectedHeight)
    {
        return RunOnStaDispatcherAsync(() =>
        {
            var preferences = new FixedCustomerDisplayWindowPreferenceStore(
                new CustomerDisplayNormalBounds(savedLeft, savedTop, savedWidth, savedHeight));
            var service = new CustomerDisplayWindowService(new DeterministicDisplayTopologyService(), preferences);
            var window = new Window
            {
                Width = 1024,
                Height = 640,
                MinWidth = 800,
                MinHeight = 520,
                WindowState = WindowState.Normal
            };

            service.ApplyModeCore(
                window,
                new Window(),
                TargetDisplay,
                CustomerDisplayWindowMode.Normal,
                showWindow: static () => { },
                setTitleBarVisible: static _ => { },
                refreshContentLayout: static () => { });

            Assert.Equal(expectedLeft, window.Left);
            Assert.Equal(expectedTop, window.Top);
            Assert.Equal(expectedWidth, window.Width);
            Assert.Equal(expectedHeight, window.Height);
        });
    }

    [Fact]
    public void Close_button_marks_window_closed_by_user_and_size_move_end_is_reported()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Windows", "CustomerDisplayWindow.xaml.cs"));
        var closeHandler = codeBehind[codeBehind.IndexOf("private void CloseButton_Click", StringComparison.Ordinal)..];

        Assert.True(
            closeHandler.IndexOf("IsClosedByUser = true;", StringComparison.Ordinal)
            < closeHandler.IndexOf("Close();", StringComparison.Ordinal));
        Assert.Contains("WmExitSizeMove = 0x0232", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MoveOrResizeCompleted?.Invoke(this, EventArgs.Empty);", codeBehind, StringComparison.Ordinal);
    }

    private static readonly DisplayBounds MainDisplay = new(
        new IntPtr(1),
        MonitorLeft: 0,
        MonitorTop: 0,
        MonitorWidth: 1920,
        MonitorHeight: 1080,
        WorkAreaLeft: 0,
        WorkAreaTop: 0,
        WorkAreaWidth: 1920,
        WorkAreaHeight: 1040);

    private static readonly DisplayBounds SecondDisplay = new(
        new IntPtr(2),
        MonitorLeft: 1920,
        MonitorTop: 0,
        MonitorWidth: 1280,
        MonitorHeight: 800,
        WorkAreaLeft: 1920,
        WorkAreaTop: 0,
        WorkAreaWidth: 1280,
        WorkAreaHeight: 800);

    [Fact]
    public Task SwapDisplays_moves_main_window_to_customer_screen_and_customer_display_to_main_screen()
    {
        return RunOnStaDispatcherAsync(() =>
        {
            var owner = new MovableOwnerWindow();
            var customerWindow = new Window { MinWidth = 800, MinHeight = 520 };
            var topology = new PerWindowDisplayTopologyService();
            topology.Assign(owner, MainDisplay);
            topology.Assign(customerWindow, SecondDisplay);
            var service = new CustomerDisplayWindowService(topology);

            var result = service.SwapDisplaysCore(
                customerWindow,
                owner,
                CustomerDisplayWindowMode.Fullscreen,
                showWindow: static () => { },
                setTitleBarVisible: static _ => { },
                refreshContentLayout: static () => { });

            Assert.Equal(CustomerDisplayWindowService.SwappedStatusKey, result.StatusMessageKey);
            Assert.Equal(CustomerDisplayWindowMode.Fullscreen, result.Mode);
            Assert.Equal([SecondDisplay], owner.MovedTo);
            // 客显全屏铺满主窗口原来那块屏的整屏边界。
            Assert.Equal(0d, customerWindow.Left);
            Assert.Equal(0d, customerWindow.Top);
            Assert.Equal(1920d, customerWindow.Width);
            Assert.Equal(1080d, customerWindow.Height);
            Assert.True(customerWindow.Topmost);
        });
    }

    [Fact]
    public Task SwapDisplays_does_nothing_when_both_windows_are_on_the_same_screen()
    {
        return RunOnStaDispatcherAsync(() =>
        {
            var owner = new MovableOwnerWindow();
            var customerWindow = new Window { Left = 10, Top = 20 };
            var topology = new PerWindowDisplayTopologyService();
            topology.Assign(owner, MainDisplay);
            topology.Assign(customerWindow, MainDisplay);
            var service = new CustomerDisplayWindowService(topology);

            var result = service.SwapDisplaysCore(
                customerWindow,
                owner,
                CustomerDisplayWindowMode.Normal,
                showWindow: static () => { },
                setTitleBarVisible: static _ => { },
                refreshContentLayout: static () => { });

            Assert.Equal(CustomerDisplayWindowService.SwapUnavailableStatusKey, result.StatusMessageKey);
            Assert.Empty(owner.MovedTo);
            Assert.Equal(10d, customerWindow.Left);
            Assert.Equal(20d, customerWindow.Top);
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

    private sealed class FixedCustomerDisplayWindowPreferenceStore(CustomerDisplayNormalBounds bounds)
        : ICustomerDisplayWindowPreferenceStore
    {
        public CustomerDisplayWindowPreference Current { get; } =
            new(CustomerDisplayWindowMode.Normal, bounds);

        public Task<CustomerDisplayWindowPreference> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task RememberModeAsync(CustomerDisplayWindowMode mode) => Task.CompletedTask;

        public Task RememberNormalBoundsAsync(CustomerDisplayNormalBounds bounds) => Task.CompletedTask;
    }

    private sealed class MovableOwnerWindow : Window, IDisplayMovableWindow
    {
        public List<DisplayBounds> MovedTo { get; } = [];

        public void MoveToDisplay(DisplayBounds display) => MovedTo.Add(display);
    }

    private sealed class PerWindowDisplayTopologyService : IDisplayTopologyService
    {
        private readonly Dictionary<Window, DisplayBounds> _displays = [];

        public void Assign(Window window, DisplayBounds display) => _displays[window] = display;

        public IReadOnlyList<DisplayBounds> GetDisplays() => _displays.Values.Distinct().ToArray();

        public DisplayBounds? FindDisplayAwayFrom(Window owner) =>
            _displays.Values.FirstOrDefault(display => display != _displays.GetValueOrDefault(owner));

        public DisplayBounds? GetDisplayForWindow(Window window) => _displays.GetValueOrDefault(window);

        public void AttachWorkAreaConstraint(Window window)
        {
        }

        public void FitToDisplayWorkArea(Window window, DisplayBounds display) =>
            ApplyBounds(window, display.WorkAreaLeft, display.WorkAreaTop, display.WorkAreaWidth, display.WorkAreaHeight);

        public void FitToDisplayBounds(Window window, DisplayBounds display) =>
            ApplyBounds(window, display.MonitorLeft, display.MonitorTop, display.MonitorWidth, display.MonitorHeight);

        private static void ApplyBounds(Window window, int left, int top, int width, int height)
        {
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
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
