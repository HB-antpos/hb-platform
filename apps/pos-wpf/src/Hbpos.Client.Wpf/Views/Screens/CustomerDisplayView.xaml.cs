using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class CustomerDisplayView : UserControl
{
    internal const double DesignCanvasHeight = 768d;
    internal const double MinDesignCanvasWidth = 1024d;
    internal const double MaxDesignCanvasWidth = 1366d;
    private static readonly GridLength VisibleSummaryRowHeight = new(152);
    private static readonly GridLength HiddenSummaryRowHeight = new(0);
    private readonly DispatcherTimer _imageAdvanceTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _videoTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CustomerDisplayViewModel? _viewModel;
    private DispatcherOperation? _pendingScrollOperation;

    public CustomerDisplayView()
    {
        InitializeComponent();
        _imageAdvanceTimer.Tick += (_, _) => AdvanceAdvertisementPlayback();
        _videoTimeoutTimer.Tick += (_, _) => SkipCurrentAdvertisementPlayback();
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
            PromotionTextPanel.Margin = new Thickness(48, 44, 48, 44);
            ApplyPromotionTypography(44, 20);
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
        PromotionTextPanel.Margin = new Thickness(48, 44, 48, 44);
        ApplyPromotionTypography(34, 16);
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
        PromotionFallbackBackground.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;
        // 广告素材播放时隐藏全部促销文字层，避免标签或说明覆盖媒体内容。
        PromotionTextPanel.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;
        // 广告素材只展示媒体本身，避免把广告名称叠在图片/视频上。
        PromotionSubtitleText.Visibility = Visibility.Collapsed;
        PromotionBodyText.Visibility = hasAdvertisement && !string.IsNullOrWhiteSpace(_viewModel?.CurrentAdvertisementDescription)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PromotionFallbackSubtitleText.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;
        PromotionFallbackBodyText.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;

        if (_viewModel?.CurrentAdvertisementMediaUrl is not { Length: > 0 } mediaUrl)
        {
            StopAdvertisementPlayback();
            return;
        }

        if (_viewModel.IsCurrentAdvertisementImage)
        {
            ShowImageAdvertisement(mediaUrl);
            return;
        }

        if (_viewModel.IsCurrentAdvertisementVideo)
        {
            ShowVideoAdvertisement(mediaUrl);
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

            AdvertisementVideo.Visibility = Visibility.Collapsed;
            AdvertisementImage.Source = bitmap;
            AdvertisementImage.Visibility = Visibility.Visible;
            _imageAdvanceTimer.Start();
        }
        catch
        {
            SkipCurrentAdvertisementPlayback();
        }
    }

    private void ShowVideoAdvertisement(string mediaUrl)
    {
        StopAdvertisementPlayback();

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri))
        {
            SkipCurrentAdvertisementPlayback();
            return;
        }

        try
        {
            AdvertisementImage.Visibility = Visibility.Collapsed;
            AdvertisementVideo.Source = mediaUri;
            AdvertisementVideo.Visibility = Visibility.Visible;
            // 视频始终静音，避免干扰收银。
            AdvertisementVideo.IsMuted = true;
            AdvertisementVideo.Volume = 0;
            AdvertisementVideo.Play();
            _videoTimeoutTimer.Start();
        }
        catch
        {
            SkipCurrentAdvertisementPlayback();
        }
    }

    private void StopAdvertisementPlayback(bool clearImageSource = true)
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();

        AdvertisementVideo.Stop();
        AdvertisementVideo.Visibility = Visibility.Collapsed;
        AdvertisementVideo.Source = null;

        AdvertisementImage.Visibility = Visibility.Collapsed;
        if (clearImageSource)
        {
            AdvertisementImage.Source = null;
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

    private void AdvertisementVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        AdvanceAdvertisementPlayback();
    }

    private void AdvertisementVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SkipCurrentAdvertisementPlayback();
    }

    private void AdvertisementImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SkipCurrentAdvertisementPlayback();
    }

    private void ApplyPromotionTypography(double subtitleFontSize, double bodyFontSize)
    {
        PromotionSubtitleText.FontSize = subtitleFontSize;
        PromotionBodyText.FontSize = bodyFontSize;
        PromotionFallbackSubtitleText.FontSize = subtitleFontSize;
        PromotionFallbackBodyText.FontSize = bodyFontSize;
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
