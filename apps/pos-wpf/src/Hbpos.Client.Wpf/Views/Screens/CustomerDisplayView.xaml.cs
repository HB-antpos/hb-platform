using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class CustomerDisplayView : UserControl
{
    internal const double DesignCanvasHeight = 768d;
    internal const double MinDesignCanvasWidth = 1024d;
    internal const double MaxDesignCanvasWidth = 1366d;
    private static readonly GridLength VisibleSummaryRowHeight = new(152);
    private static readonly GridLength HiddenSummaryRowHeight = new(0);
    private static readonly TimeSpan DefaultImageDisplayDuration = TimeSpan.FromSeconds(8);
    // 动图至少完整播一轮再切走，但和视频一样不超过 30 秒。
    private static readonly TimeSpan MaxAnimatedImageDisplayDuration = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _imageAdvanceTimer = new() { Interval = DefaultImageDisplayDuration };
    private readonly DispatcherTimer _gifFrameTimer = new(DispatcherPriority.Render);
    private AnimatedGifRenderer? _activeGif;
    private int _activeGifFrameIndex;
    private readonly DispatcherTimer _videoTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    // 打开耗时超过这个值的视频即使不是首次播放也记日志，用来发现解码器卡顿。
    private static readonly TimeSpan SlowVideoOpenThreshold = TimeSpan.FromSeconds(3);
    private const int MaxLoggedOpenedVideoKeys = 200;
    private const string LogCategory = "CustomerDisplay";
    // 空闲客显每几秒就切一条视频，MediaOpened 每次都记会刷爆中心日志；只记每个素材第一次打开（以及打开慢的）。
    private readonly HashSet<string> _loggedOpenedVideoKeys = new(StringComparer.Ordinal);
    private CustomerDisplayViewModel? _viewModel;
    private DispatcherOperation? _pendingScrollOperation;
    private MediaElement? _activeVideoPlayer;
    private Uri? _activeVideoUri;
    private AdvertisementPlaybackItemDto? _activeVideoAdvertisement;
    private long _activeVideoStartTimestamp;
    private bool _activeVideoOpened;
    // 超时已重试过一次的广告 Id；换到别的广告或这条正常播完后清空。
    private string? _videoTimeoutRetriedAdvertisementId;
    // 为播放视频临时切成软件渲染的宿主窗口；不播视频时恢复硬件渲染。
    private HwndSource? _softwareRenderedVideoHost;
    // 图片、视频混播时每轮都会切换渲染模式，只记第一次。
    private bool _softwareVideoRenderingLogged;

    public CustomerDisplayView()
    {
        InitializeComponent();
        _imageAdvanceTimer.Tick += (_, _) => AdvanceAdvertisementPlayback();
        _gifFrameTimer.Tick += (_, _) => AdvanceGifFrame();
        _videoTimeoutTimer.Tick += (_, _) => HandleVideoTimeout();
        Loaded += CustomerDisplayViewLoaded;
        DataContextChanged += CustomerDisplayViewDataContextChanged;
        Unloaded += CustomerDisplayViewUnloaded;
        SizeChanged += CustomerDisplayViewSizeChanged;
    }

    private void CustomerDisplayViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 按宿主尺寸调整画布宽度，Viewbox 再整体等比缩放；画布变化不影响宿主尺寸，不会形成布局回环。
        var canvasWidth = ResolveDesignCanvasWidth(e.NewSize.Width, e.NewSize.Height);
        DesignCanvas.Width = canvasWidth;
        // 画布变窄时购物车列多分一些，避免商品名被右侧广告挤到只剩几个字。
        var cartShare = ResolveCartColumnShare(canvasWidth);
        CartColumn.Width = new GridLength(cartShare, GridUnitType.Star);
        PromotionColumn.Width = new GridLength(1d - cartShare, GridUnitType.Star);
    }

    internal static double ResolveCartColumnShare(double canvasWidth)
    {
        const double wideShare = 0.60d;
        const double narrowShare = 0.68d;
        var narrowness = (MaxDesignCanvasWidth - Math.Clamp(canvasWidth, MinDesignCanvasWidth, MaxDesignCanvasWidth))
            / (MaxDesignCanvasWidth - MinDesignCanvasWidth);
        return wideShare + ((narrowShare - wideShare) * narrowness);
    }

    internal static double ResolveDesignCanvasWidth(double hostWidth, double hostHeight)
    {
        if (!double.IsFinite(hostWidth)
            || !double.IsFinite(hostHeight)
            || hostWidth <= 0d
            || hostHeight <= 0d)
        {
            return MaxDesignCanvasWidth;
        }

        // 高度固定，宽度按宿主宽高比推算；比 16:9 更宽的屏保持 1366 两侧留白，比 4:3 更窄的屏保持 1024 上下留白。
        return Math.Clamp(DesignCanvasHeight * hostWidth / hostHeight, MinDesignCanvasWidth, MaxDesignCanvasWidth);
    }

    private void CustomerDisplayViewLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(DataContext as CustomerDisplayViewModel);
        RefreshPromotionLayout();
        RefreshAdvertisementPlayback();
    }

    private void CustomerDisplayViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CancelPendingScroll();
        UnsubscribeFromViewModel();
        SubscribeToViewModel(e.NewValue as CustomerDisplayViewModel);
        RefreshPromotionLayout();
        RefreshAdvertisementPlayback();
    }

    private void SubscribeToViewModel(CustomerDisplayViewModel? viewModel)
    {
        if (_viewModel is not null || viewModel is null)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.Lines.CollectionChanged += LinesCollectionChanged;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        ScrollLatestLineIntoView();
    }

    private void CustomerDisplayViewUnloaded(object sender, RoutedEventArgs e)
    {
        StopAdvertisementPlayback();
        RestoreHardwareVideoRendering();
        UnsubscribeFromViewModel();
    }

    private void LinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollLatestLineIntoView();
    }

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyPromotionLayout(e.NewSize.Width);
    }

    public void RefreshPromotionLayout()
    {
        ApplyPromotionLayout(ContentGrid.ActualWidth);
    }

    private void ApplyPromotionLayout(double width)
    {
        if (_viewModel?.IsIdleAdvertisementVisible == true)
        {
            // 空闲状态使用全屏广告布局。
            SummaryRow.Height = HiddenSummaryRowHeight;
            SummaryPanel.Visibility = Visibility.Collapsed;
            CartPanel.Visibility = Visibility.Collapsed;
            PromotionBannerRow.Height = new GridLength(0);
            Grid.SetRow(PromotionPanel, 0);
            Grid.SetColumn(PromotionPanel, 0);
            Grid.SetRowSpan(PromotionPanel, 2);
            Grid.SetColumnSpan(PromotionPanel, 2);
            PromotionPanel.Margin = new Thickness(0);
            return;
        }

        SummaryRow.Height = VisibleSummaryRowHeight;
        SummaryPanel.Visibility = Visibility.Visible;
        CartPanel.Visibility = Visibility.Visible;
        Grid.SetRowSpan(PromotionPanel, 1);

        // 购物车有商品时广告固定在右侧，避免窄屏横幅布局遮盖购物车。
        PromotionBannerRow.Height = new GridLength(0);
        Grid.SetRow(PromotionPanel, 1);
        Grid.SetColumn(PromotionPanel, 1);
        Grid.SetColumnSpan(PromotionPanel, 1);
        PromotionPanel.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumnSpan(CartPanel, 1);
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomerDisplayViewModel.IsIdleAdvertisementVisible))
        {
            RefreshPromotionLayout();
        }

        if (e.PropertyName is nameof(CustomerDisplayViewModel.CurrentAdvertisement)
            or nameof(CustomerDisplayViewModel.IsAdvertisementAvailable))
        {
            RefreshAdvertisementPlayback();
        }
    }

    private void RefreshAdvertisementPlayback()
    {
        var hasAdvertisement = _viewModel?.IsAdvertisementAvailable == true;
        // 有广告素材时收起默认背景，避免图片/视频被后层渐变遮住。
        // 已取消内置的默认促销文案：没有后台广告时广告区只保留纯底色，不再显示任何文字。
        PromotionFallbackBackground.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;

        if (_viewModel?.CurrentAdvertisementMediaUrl is not { Length: > 0 } mediaUrl)
        {
            StopAdvertisementPlayback();
            RestoreHardwareVideoRendering();
            return;
        }

        if (_viewModel.IsCurrentAdvertisementVideo)
        {
            ShowVideoAdvertisement(mediaUrl);
            return;
        }

        // 视频之间切换时保持软件渲染，避免每条都来回切换渲染模式；只有轮到图片或没有广告才恢复。
        RestoreHardwareVideoRendering();
        if (_viewModel.IsCurrentAdvertisementImage)
        {
            ShowImageAdvertisement(mediaUrl);
            return;
        }

        StopAdvertisementPlayback();
    }

    private void ShowImageAdvertisement(string mediaUrl)
    {
        StopAdvertisementPlayback(clearImageSource: false);

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri))
        {
            SkipCurrentAdvertisementPlayback();
            return;
        }

        if (TryShowAnimatedGif(mediaUri))
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = mediaUri;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            // IgnoreImageCache 已绕开 WPF 的图像缓存，若再用默认的 OnDemand，
            // 位图会一直持有素材文件的流，缓存清理的 File.Delete 会失败、过期素材持续累积。
            // 客显空闲广告每 8 秒轮换、收银机连开数天，这个占用会不断堆积。
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            // 冻结后可跨线程访问，并省去后续的变更通知开销。
            bitmap.Freeze();

            AdvertisementVideoHost.Visibility = Visibility.Collapsed;
            AdvertisementImage.Source = bitmap;
            AdvertisementImage.Visibility = Visibility.Visible;
            _imageAdvanceTimer.Interval = DefaultImageDisplayDuration;
            _imageAdvanceTimer.Start();
        }
        catch
        {
            SkipCurrentAdvertisementPlayback();
        }
    }

    private bool TryShowAnimatedGif(Uri mediaUri)
    {
        // 只处理已缓存到本地的 GIF；远程地址（缓存失败的回退）仍按静态图片显示第一帧。
        if (!mediaUri.IsFile
            || !string.Equals(Path.GetExtension(mediaUri.LocalPath), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AnimatedGifRenderer? renderer;
        try
        {
            renderer = AnimatedGifRenderer.TryCreate(File.ReadAllBytes(mediaUri.LocalPath));
            renderer?.RenderFrame(0);
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteWarning(
                LogCategory,
                $"advertisement gif animation unavailable, showing first frame {DescribeAdvertisement(_viewModel?.CurrentAdvertisement, mediaUri)}",
                exception: ex);
            return false;
        }

        if (renderer is null)
        {
            // 单帧 GIF 走普通图片路径即可。
            return false;
        }

        _activeGif = renderer;
        _activeGifFrameIndex = 0;
        AdvertisementVideoHost.Visibility = Visibility.Collapsed;
        AdvertisementImage.Source = renderer.Bitmap;
        AdvertisementImage.Visibility = Visibility.Visible;
        _gifFrameTimer.Interval = renderer.GetFrameDelay(0);
        _gifFrameTimer.Start();
        _imageAdvanceTimer.Interval = ResolveImageDisplayDuration(renderer.LoopDuration);
        _imageAdvanceTimer.Start();
        return true;
    }

    private void AdvanceGifFrame()
    {
        if (_activeGif is not { } renderer)
        {
            _gifFrameTimer.Stop();
            return;
        }

        _activeGifFrameIndex = (_activeGifFrameIndex + 1) % renderer.FrameCount;
        try
        {
            renderer.RenderFrame(_activeGifFrameIndex);
        }
        catch (Exception ex)
        {
            // 个别帧解码失败时停在当前画面，到点照常轮换，不影响后续广告。
            StopAnimatedGif();
            ConsoleLog.WriteWarning(
                LogCategory,
                $"advertisement gif frame failed frame={_activeGifFrameIndex} {DescribeAdvertisement(_viewModel?.CurrentAdvertisement, null)}",
                exception: ex);
            return;
        }

        _gifFrameTimer.Interval = renderer.GetFrameDelay(_activeGifFrameIndex);
    }

    private void StopAnimatedGif()
    {
        _gifFrameTimer.Stop();
        _activeGif = null;
    }

    internal static TimeSpan ResolveImageDisplayDuration(TimeSpan animationLoopDuration)
    {
        if (animationLoopDuration <= DefaultImageDisplayDuration)
        {
            return DefaultImageDisplayDuration;
        }

        return animationLoopDuration < MaxAnimatedImageDisplayDuration
            ? animationLoopDuration
            : MaxAnimatedImageDisplayDuration;
    }

    private void ShowVideoAdvertisement(string mediaUrl)
    {
        StopAdvertisementPlayback();
        var advertisement = _viewModel?.CurrentAdvertisement;

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri))
        {
            ConsoleLog.WriteWarning(
                LogCategory,
                $"advertisement video skipped reason=invalid-url {DescribeAdvertisement(advertisement, null)}");
            SkipCurrentAdvertisementPlayback();
            return;
        }

        if (!string.Equals(advertisement?.Id, _videoTimeoutRetriedAdvertisementId, StringComparison.Ordinal))
        {
            _videoTimeoutRetriedAdvertisementId = null;
        }

        StartVideoPlayer(mediaUri, advertisement);
    }

    private void StartVideoPlayer(Uri mediaUri, AdvertisementPlaybackItemDto? advertisement)
    {
        // 每条视频新建一个 MediaElement、播完销毁：复用同一个控件换源时，显卡硬解码器可能残留上一条视频的状态，
        // 实测多条视频轮播会花屏或卡住打不开，而单独播任意一条都正常。
        var player = new MediaElement
        {
            Stretch = Stretch.Uniform,
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            // 视频始终静音，避免干扰收银。
            IsMuted = true,
            Volume = 0,
        };
        player.MediaOpened += AdvertisementVideo_MediaOpened;
        player.MediaEnded += AdvertisementVideo_MediaEnded;
        player.MediaFailed += AdvertisementVideo_MediaFailed;

        _activeVideoPlayer = player;
        _activeVideoUri = mediaUri;
        _activeVideoAdvertisement = advertisement;
        _activeVideoOpened = false;
        _activeVideoStartTimestamp = Stopwatch.GetTimestamp();

        try
        {
            ApplyVideoRenderMode(advertisement);
            AdvertisementImage.Visibility = Visibility.Collapsed;
            AdvertisementVideoHost.Child = player;
            AdvertisementVideoHost.Visibility = Visibility.Visible;
            // 两种 Behavior 都是 Manual，Play 不依赖控件是否已 Loaded。
            player.Source = mediaUri;
            player.Play();
            _videoTimeoutTimer.Start();
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteWarning(
                LogCategory,
                $"advertisement video skipped reason=start-failed {DescribeAdvertisement(advertisement, mediaUri)}",
                exception: ex);
            SkipCurrentAdvertisementPlayback();
        }
    }

    private void ApplyVideoRenderMode(AdvertisementPlaybackItemDto? advertisement)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } compositionTarget } source)
        {
            return;
        }

        var window = Window.GetWindow(this);
        var display = window is null ? null : DisplayTopologyService.FindDisplayForWindow(window);
        if (!ShouldUseSoftwareVideoRendering(display))
        {
            RestoreHardwareVideoRendering();
            return;
        }

        if (ReferenceEquals(_softwareRenderedVideoHost, source))
        {
            return;
        }

        RestoreHardwareVideoRendering();
        if (compositionTarget.RenderMode == RenderMode.SoftwareOnly)
        {
            // 窗口本来就是软件渲染（不是这里切的），不接管恢复。
            return;
        }

        compositionTarget.RenderMode = RenderMode.SoftwareOnly;
        _softwareRenderedVideoHost = source;
        if (_softwareVideoRenderingLogged)
        {
            return;
        }

        _softwareVideoRenderingLogged = true;
        ConsoleLog.Write(
            LogCategory,
            $"advertisement video render mode=software reason=non-primary-monitor "
            + $"monitor={display!.MonitorLeft},{display.MonitorTop},{display.MonitorWidth}x{display.MonitorHeight} "
            + DescribeAdvertisement(advertisement, null));
    }

    /// <summary>
    /// WPF 的视频表面建在主显示器对应的 D3D9 适配器上，窗口在副屏时硬件合成拿不到新帧：
    /// 播放进度和 MediaEnded 都正常，画面却停在第一帧（淡入类视频第一帧多为黑屏）。
    /// 客显几乎总在副屏，这时改由软件渲染，主屏保持硬件渲染不受影响。
    /// </summary>
    internal static bool ShouldUseSoftwareVideoRendering(DisplayBounds? display)
    {
        // 主显示器的左上角固定是虚拟屏幕原点；拿不到显示器信息时保持硬件渲染。
        return display is not null && (display.MonitorLeft != 0 || display.MonitorTop != 0);
    }

    private void RestoreHardwareVideoRendering()
    {
        if (_softwareRenderedVideoHost is not { } source)
        {
            return;
        }

        _softwareRenderedVideoHost = null;
        if (!source.IsDisposed && source.CompositionTarget is { } compositionTarget)
        {
            // 软件渲染整窗 CPU 开销明显，没有视频在播时切回硬件渲染。
            compositionTarget.RenderMode = RenderMode.Default;
        }
    }

    private void StopAdvertisementPlayback(bool clearImageSource = true)
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();
        StopAnimatedGif();

        DisposeActiveVideoPlayer();
        AdvertisementVideoHost.Visibility = Visibility.Collapsed;

        AdvertisementImage.Visibility = Visibility.Collapsed;
        if (clearImageSource)
        {
            AdvertisementImage.Source = null;
        }
    }

    private void DisposeActiveVideoPlayer()
    {
        if (_activeVideoPlayer is not { } player)
        {
            return;
        }

        _activeVideoPlayer = null;
        _activeVideoUri = null;
        _activeVideoAdvertisement = null;
        player.MediaOpened -= AdvertisementVideo_MediaOpened;
        player.MediaEnded -= AdvertisementVideo_MediaEnded;
        player.MediaFailed -= AdvertisementVideo_MediaFailed;

        try
        {
            player.Stop();
            // Close 释放底层媒体会话与解码器，下一条视频用全新的播放器打开。
            player.Close();
            player.Source = null;
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteWarning(LogCategory, "advertisement video player dispose failed", exception: ex);
        }
        finally
        {
            AdvertisementVideoHost.Child = null;
        }
    }

    private void AdvanceAdvertisementPlayback()
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();
        _viewModel?.AdvanceAdvertisement();
    }

    private void SkipCurrentAdvertisementPlayback()
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();
        _viewModel?.SkipCurrentAdvertisement();
    }

    private void AdvertisementVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activeVideoPlayer) || _activeVideoPlayer is not { } player)
        {
            return;
        }

        _activeVideoOpened = true;
        var openElapsed = Stopwatch.GetElapsedTime(_activeVideoStartTimestamp);
        var key = $"{_activeVideoAdvertisement?.Id}|{_activeVideoUri}";
        var isFirstOpen = _loggedOpenedVideoKeys.Add(key);
        if (_loggedOpenedVideoKeys.Count > MaxLoggedOpenedVideoKeys)
        {
            _loggedOpenedVideoKeys.Clear();
        }

        if (!isFirstOpen && openElapsed < SlowVideoOpenThreshold)
        {
            return;
        }

        var duration = player.NaturalDuration.HasTimeSpan
            ? player.NaturalDuration.TimeSpan.TotalMilliseconds.ToString("0")
            : "unknown";
        ConsoleLog.Write(
            LogCategory,
            $"advertisement video opened reason={(isFirstOpen ? "first-open" : "slow-open")} openMs={openElapsed.TotalMilliseconds:0} "
            + $"size={player.NaturalVideoWidth}x{player.NaturalVideoHeight} durationMs={duration} "
            + DescribeAdvertisement(_activeVideoAdvertisement, _activeVideoUri));
    }

    private void AdvertisementVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activeVideoPlayer))
        {
            return;
        }

        _videoTimeoutTimer.Stop();
        // 这条完整播完，超时重试标记作废。
        _videoTimeoutRetriedAdvertisementId = null;
        // 延后到 MediaEnded 处理完之后再切换：在事件里同步停止并换源，播放器还没收尾就开始打开下一条。
        RunAfterCurrentVideoEvent(AdvanceAdvertisementPlayback);
    }

    private void AdvertisementVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activeVideoPlayer))
        {
            return;
        }

        _videoTimeoutTimer.Stop();
        ConsoleLog.WriteWarning(
            LogCategory,
            $"advertisement video skipped reason=media-failed opened={_activeVideoOpened} "
            + $"elapsedMs={Stopwatch.GetElapsedTime(_activeVideoStartTimestamp).TotalMilliseconds:0} "
            + DescribeAdvertisement(_activeVideoAdvertisement, _activeVideoUri),
            exception: e.ErrorException);
        RunAfterCurrentVideoEvent(SkipCurrentAdvertisementPlayback);
    }

    private void RunAfterCurrentVideoEvent(Action action)
    {
        var player = _activeVideoPlayer;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                // 排队期间如果已经换了播放器（订单行变化、广告列表刷新等），这个事件就过时了，不能再推进轮播。
                if (ReferenceEquals(player, _activeVideoPlayer))
                {
                    action();
                }
            }),
            DispatcherPriority.Background);
    }

    private void HandleVideoTimeout()
    {
        _videoTimeoutTimer.Stop();
        if (_activeVideoPlayer is not { } player || _activeVideoUri is not { } mediaUri)
        {
            SkipCurrentAdvertisementPlayback();
            return;
        }

        var advertisement = _activeVideoAdvertisement;
        TimeSpan? naturalDuration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : null;
        var alreadyRetried = advertisement is not null
            && string.Equals(advertisement.Id, _videoTimeoutRetriedAdvertisementId, StringComparison.Ordinal);
        var action = ResolveVideoTimeoutAction(
            _activeVideoOpened,
            naturalDuration,
            _videoTimeoutTimer.Interval,
            alreadyRetried,
            canRetry: advertisement is not null);

        ConsoleLog.WriteWarning(
            LogCategory,
            $"advertisement video timeout action={action} opened={_activeVideoOpened} "
            + $"positionMs={player.Position.TotalMilliseconds:0} "
            + $"durationMs={(naturalDuration is { } duration ? duration.TotalMilliseconds.ToString("0") : "unknown")} "
            + DescribeAdvertisement(advertisement, mediaUri));

        if (action == VideoTimeoutAction.RetryOnce)
        {
            _videoTimeoutRetriedAdvertisementId = advertisement!.Id;
            StopAdvertisementPlayback();
            StartVideoPlayer(mediaUri, advertisement);
            return;
        }

        SkipCurrentAdvertisementPlayback();
    }

    internal enum VideoTimeoutAction
    {
        RetryOnce,
        Skip,
    }

    internal static VideoTimeoutAction ResolveVideoTimeoutAction(
        bool opened,
        TimeSpan? naturalDuration,
        TimeSpan timeout,
        bool alreadyRetried,
        bool canRetry)
    {
        // 素材本身就超过单条时长上限：重试也播不完，沿用原来的处理，直接移出本轮。
        if (opened && naturalDuration is { } duration && duration >= timeout)
        {
            return VideoTimeoutAction.Skip;
        }

        // 卡住（没打开或停在中途）先换一个全新的播放器重试一次，还不行再移出本轮，等下次定时刷新重新加载。
        return canRetry && !alreadyRetried ? VideoTimeoutAction.RetryOnce : VideoTimeoutAction.Skip;
    }

    private static string DescribeAdvertisement(AdvertisementPlaybackItemDto? advertisement, Uri? mediaUri)
    {
        // 只记文件名，不记完整地址，避免把可能带签名参数的 URL 写进中心日志。
        var fileName = mediaUri is null ? string.Empty : Path.GetFileName(mediaUri.IsFile ? mediaUri.LocalPath : mediaUri.AbsolutePath);
        return $"advertisementId={advertisement?.Id} title={advertisement?.Title} file={fileName}";
    }

    private void AdvertisementImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SkipCurrentAdvertisementPlayback();
    }

    private void ScrollLatestLineIntoView()
    {
        if (_pendingScrollOperation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing })
        {
            return;
        }

        _pendingScrollOperation = LineDataGrid.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _pendingScrollOperation = null;
                var latestLine = _viewModel?.Lines.LastOrDefault();
                if (latestLine is null)
                {
                    return;
                }

                LineDataGrid.ScrollIntoView(latestLine);
            }),
            DispatcherPriority.Background);
    }

    private void CancelPendingScroll()
    {
        _pendingScrollOperation?.Abort();
        _pendingScrollOperation = null;
    }

    private void UnsubscribeFromViewModel()
    {
        CancelPendingScroll();
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged -= LinesCollectionChanged;
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            _viewModel = null;
        }
    }
}
