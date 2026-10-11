using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 设置页「员工 App 下载」：展示 iPhone / 安卓（含 PDA）的安装二维码。
/// 二维码内容是固定链接，进入页面即可显示；版本、包大小与安卓包是否已发布由联网查询补充。
/// </summary>
public sealed class MobileAppDownloadViewModel : ObservableObject
{
    public const string IosPlatform = "ios";
    public const string AndroidPlatform = "android";

    private readonly IMobileAppDownloadService? _service;
    private readonly ILocalizationService? _localization;
    private readonly Action<string> _copyToClipboard;
    private MobileAppInstallInfo _info;
    private bool _isLoading;
    private bool _hasAttempted;
    private string _copyFeedbackText = string.Empty;
    private string? _enlargedPlatform;
    // 连续进入页面时可能有多次查询在途，只采用最后一次发起的结果，避免慢的旧响应覆盖新数据。
    private int _loadSequence;

    public MobileAppDownloadViewModel(
        IMobileAppDownloadService? service = null,
        ILocalizationService? localization = null,
        Action<string>? copyToClipboard = null)
    {
        _service = service;
        _localization = localization;
        _copyToClipboard = copyToClipboard ?? (text => Clipboard.SetText(text));
        _info = service?.GetLocalLinks() ?? MobileAppDownloadService.BuildLocalLinks(new Uri(MobileAppDownloadService.DefaultCenterBaseUrl));

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        CopyLinkCommand = new RelayCommand<string>(CopyLink);
        EnlargeCommand = new RelayCommand<string>(Enlarge);
        CloseEnlargedCommand = new RelayCommand(CloseEnlarged);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand<string> CopyLinkCommand { get; }

    public IRelayCommand<string> EnlargeCommand { get; }

    public IRelayCommand CloseEnlargedCommand { get; }

    public string IosQrPayload => _info.IosUrl;

    public string AndroidQrPayload => _info.AndroidUrl;

    // 链接展示去掉协议头，卡片里一行放得下更多有用的部分。
    public string IosLinkText => StripScheme(_info.IosUrl);

    public string AndroidLinkText => StripScheme(_info.AndroidUrl);

    public string IosVersionText => _info.IosVersion ?? T("mobileApp.versionUnknown");

    public string AndroidVersionText =>
        _info.AndroidVersion is null
            ? T("mobileApp.versionUnknown")
            : _info.AndroidBuild is null
                ? _info.AndroidVersion
                : string.Format(Culture, T("mobileApp.android.versionFormat"), _info.AndroidVersion, _info.AndroidBuild);

    public bool HasAndroidSize => _info.AndroidSizeBytes is > 0;

    public string AndroidSizeText =>
        _info.AndroidSizeBytes is > 0 and var bytes
            ? string.Format(Culture, T("mobileApp.android.sizeFormat"), Math.Max(1, (long)Math.Round(bytes / 1048576.0)))
            : string.Empty;

    /// <summary>总部确认「没有可下载的安卓包」时为 false；联网失败（未知）仍按可用显示，二维码是固定链接。</summary>
    public bool IsAndroidAvailable => _info.AndroidAvailability != MobileAppAndroidAvailability.NotPublished;

    public bool IsAndroidNotPublished => !IsAndroidAvailable;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsOfflineWarning));
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>查询过但没联上总部：版本信息不可用，二维码仍可正常扫。</summary>
    public bool IsOfflineWarning => !IsLoading && _hasAttempted && !_info.MetadataLoaded;

    public string StatusText =>
        IsLoading ? T("mobileApp.status.loading")
        : !_hasAttempted ? string.Empty
        : _info.MetadataLoaded ? T("mobileApp.status.latest")
        : T("mobileApp.status.offline");

    public string CopyFeedbackText
    {
        get => _copyFeedbackText;
        private set
        {
            if (SetProperty(ref _copyFeedbackText, value))
            {
                OnPropertyChanged(nameof(HasCopyFeedback));
            }
        }
    }

    public bool HasCopyFeedback => !string.IsNullOrEmpty(_copyFeedbackText);

    public bool IsEnlarged => _enlargedPlatform is not null;

    public string EnlargedQrPayload =>
        _enlargedPlatform switch
        {
            IosPlatform => _info.IosUrl,
            AndroidPlatform => _info.AndroidUrl,
            _ => string.Empty
        };

    public string EnlargedTitleText =>
        _enlargedPlatform switch
        {
            IosPlatform => T("mobileApp.ios.title"),
            AndroidPlatform => T("mobileApp.android.title"),
            _ => string.Empty
        };

    /// <summary>进入页面（或点刷新）时调用：向总部补充版本信息。没有服务时什么都不做，仍显示本地链接。</summary>
    public async Task LoadAsync()
    {
        if (_service is null)
        {
            return;
        }

        var sequence = ++_loadSequence;
        CopyFeedbackText = string.Empty;
        IsLoading = true;
        try
        {
            var info = await _service.RefreshAsync().ConfigureAwait(true);
            if (sequence == _loadSequence)
            {
                ApplyInfo(info);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 服务本身不会抛出；万一出现意外异常，也只当作「没联上总部」，不影响二维码使用。
            if (sequence == _loadSequence)
            {
                ApplyInfo(_info with { MetadataLoaded = false });
            }
        }
        finally
        {
            if (sequence == _loadSequence)
            {
                _hasAttempted = true;
                IsLoading = false;
            }
        }
    }

    /// <summary>切换语言后刷新所有带本地化文案的属性。</summary>
    public void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(IosVersionText));
        OnPropertyChanged(nameof(AndroidVersionText));
        OnPropertyChanged(nameof(AndroidSizeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EnlargedTitleText));
        CopyFeedbackText = string.Empty;
    }

    private void ApplyInfo(MobileAppInstallInfo info)
    {
        _info = info;
        OnPropertyChanged(nameof(IosQrPayload));
        OnPropertyChanged(nameof(AndroidQrPayload));
        OnPropertyChanged(nameof(IosLinkText));
        OnPropertyChanged(nameof(AndroidLinkText));
        OnPropertyChanged(nameof(IosVersionText));
        OnPropertyChanged(nameof(AndroidVersionText));
        OnPropertyChanged(nameof(HasAndroidSize));
        OnPropertyChanged(nameof(AndroidSizeText));
        OnPropertyChanged(nameof(IsAndroidAvailable));
        OnPropertyChanged(nameof(IsAndroidNotPublished));
        OnPropertyChanged(nameof(IsOfflineWarning));
        OnPropertyChanged(nameof(StatusText));
    }

    private void CopyLink(string? platform)
    {
        var url = string.Equals(platform, AndroidPlatform, StringComparison.OrdinalIgnoreCase)
            ? _info.AndroidUrl
            : _info.IosUrl;
        try
        {
            _copyToClipboard(url);
            CopyFeedbackText = T("mobileApp.copied");
        }
        catch (ExternalException)
        {
            // 剪贴板被其他程序占用时会抛 COMException；提示失败即可，不能让设置页崩溃。
            CopyFeedbackText = T("mobileApp.copyFailed");
        }
    }

    private void Enlarge(string? platform)
    {
        _enlargedPlatform = string.Equals(platform, AndroidPlatform, StringComparison.OrdinalIgnoreCase)
            ? AndroidPlatform
            : IosPlatform;
        RaiseEnlargedProperties();
    }

    private void CloseEnlarged()
    {
        _enlargedPlatform = null;
        RaiseEnlargedProperties();
    }

    private void RaiseEnlargedProperties()
    {
        OnPropertyChanged(nameof(IsEnlarged));
        OnPropertyChanged(nameof(EnlargedQrPayload));
        OnPropertyChanged(nameof(EnlargedTitleText));
    }

    private static string StripScheme(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host + uri.PathAndQuery.TrimEnd('/')
            : url;

    private CultureInfo Culture => _localization?.CurrentCulture ?? CultureInfo.CurrentCulture;

    private string T(string key) => _localization?.T(key) ?? LocalizationResourceProvider.Instance[key];
}
