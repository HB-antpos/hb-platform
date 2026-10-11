using System.Runtime.InteropServices;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

public sealed class MobileAppDownloadViewModelTests
{
    private static readonly MobileAppInstallInfo Loaded = new(
        "https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4",
        "1.0.7",
        "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production",
        MobileAppAndroidAvailability.Available,
        "1.0.10",
        "63",
        124_168_826,
        MetadataLoaded: true);

    [Fact]
    public void QR_links_are_available_immediately_without_waiting_for_the_network()
    {
        var service = new FakeMobileAppDownloadService();
        var viewModel = new MobileAppDownloadViewModel(service, English());

        Assert.Equal(MobileAppDownloadService.BuildLocalLinks(new Uri(MobileAppDownloadService.DefaultCenterBaseUrl)).IosUrl, viewModel.IosQrPayload);
        Assert.Contains("/api/mobile-app-builds/android-latest/download", viewModel.AndroidQrPayload, StringComparison.Ordinal);
        Assert.True(viewModel.IsAndroidAvailable);
        Assert.False(viewModel.IsLoading);
        Assert.Equal(string.Empty, viewModel.StatusText);
        Assert.Equal(0, service.RefreshCalls);
    }

    [Fact]
    public void Without_a_service_the_page_still_shows_fixed_links_and_load_is_a_no_op()
    {
        var viewModel = new MobileAppDownloadViewModel(service: null, English());

        Assert.False(string.IsNullOrWhiteSpace(viewModel.IosQrPayload));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.AndroidQrPayload));
        Assert.True(viewModel.LoadAsync().IsCompletedSuccessfully);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task Load_applies_versions_size_and_the_up_to_date_status()
    {
        var localization = English();
        var service = new FakeMobileAppDownloadService { OnRefresh = () => Task.FromResult(Loaded) };
        var viewModel = new MobileAppDownloadViewModel(service, localization);

        await viewModel.LoadAsync();

        Assert.Equal(Loaded.IosUrl, viewModel.IosQrPayload);
        Assert.Equal("1.0.7", viewModel.IosVersionText);
        Assert.Equal("1.0.10 (build 63)", viewModel.AndroidVersionText);
        Assert.True(viewModel.HasAndroidSize);
        Assert.Equal("About 118 MB", viewModel.AndroidSizeText);
        Assert.Equal(localization.T("mobileApp.status.latest"), viewModel.StatusText);
        Assert.False(viewModel.IsOfflineWarning);
        Assert.False(viewModel.IsLoading);
        Assert.Equal("apps.apple.com/au/app/hbgroup/id6786073002?uo=4", viewModel.IosLinkText);
    }

    [Fact]
    public async Task Offline_keeps_the_QR_codes_and_only_warns_that_version_info_is_unavailable()
    {
        var localization = English();
        // 联网失败时服务退回的就是本地链接（MetadataLoaded=false）。
        var offline = MobileAppDownloadService.BuildLocalLinks(new Uri(MobileAppDownloadService.DefaultCenterBaseUrl));
        var service = new FakeMobileAppDownloadService { OnRefresh = () => Task.FromResult(offline) };
        var viewModel = new MobileAppDownloadViewModel(service, localization);
        var qrBefore = (viewModel.IosQrPayload, viewModel.AndroidQrPayload);

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsOfflineWarning);
        Assert.Equal(localization.T("mobileApp.status.offline"), viewModel.StatusText);
        Assert.Equal(qrBefore, (viewModel.IosQrPayload, viewModel.AndroidQrPayload));
        Assert.True(viewModel.IsAndroidAvailable);
        Assert.Equal(localization.T("mobileApp.versionUnknown"), viewModel.AndroidVersionText);
        Assert.False(viewModel.HasAndroidSize);
    }

    [Fact]
    public async Task A_confirmed_missing_android_package_replaces_the_QR_with_the_unpublished_state()
    {
        var service = new FakeMobileAppDownloadService
        {
            OnRefresh = () => Task.FromResult(Loaded with
            {
                AndroidAvailability = MobileAppAndroidAvailability.NotPublished,
                AndroidVersion = null,
                AndroidBuild = null,
                AndroidSizeBytes = null
            })
        };
        var viewModel = new MobileAppDownloadViewModel(service, English());

        await viewModel.LoadAsync();

        Assert.False(viewModel.IsAndroidAvailable);
        Assert.True(viewModel.IsAndroidNotPublished);
    }

    [Fact]
    public async Task Switching_the_language_refreshes_the_localized_texts()
    {
        var localization = English();
        var service = new FakeMobileAppDownloadService { OnRefresh = () => Task.FromResult(Loaded) };
        var viewModel = new MobileAppDownloadViewModel(service, localization);
        await viewModel.LoadAsync();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        localization.SetCulture("zh-CN");
        viewModel.RaiseLocalizedProperties();

        Assert.Equal("约 118 MB", viewModel.AndroidSizeText);
        Assert.Equal("1.0.10（构建 63）", viewModel.AndroidVersionText);
        Assert.Contains(nameof(MobileAppDownloadViewModel.StatusText), changed);
        Assert.Contains(nameof(MobileAppDownloadViewModel.AndroidSizeText), changed);
    }

    [Fact]
    public async Task A_slow_older_response_never_overwrites_the_newest_result()
    {
        var older = new TaskCompletionSource<MobileAppInstallInfo>();
        var responses = new Queue<Func<Task<MobileAppInstallInfo>>>(
        [
            () => older.Task,
            () => Task.FromResult(Loaded with { AndroidVersion = "1.0.11" })
        ]);
        var service = new FakeMobileAppDownloadService { OnRefresh = () => responses.Dequeue()() };
        var viewModel = new MobileAppDownloadViewModel(service, English());

        var first = viewModel.LoadAsync();
        await viewModel.LoadAsync();
        older.SetResult(Loaded with { AndroidVersion = "1.0.9" });
        await first;

        Assert.StartsWith("1.0.11", viewModel.AndroidVersionText, StringComparison.Ordinal);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task The_refresh_command_is_unavailable_while_a_lookup_is_in_flight()
    {
        var gate = new TaskCompletionSource<MobileAppInstallInfo>();
        var service = new FakeMobileAppDownloadService { OnRefresh = () => gate.Task };
        var viewModel = new MobileAppDownloadViewModel(service, English());

        var load = viewModel.LoadAsync();

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));

        gate.SetResult(Loaded);
        await load;

        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("ios", "https://apps.apple.com/au/app/hbgroup/id6786073002")]
    [InlineData("android", "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production")]
    public void Copy_link_puts_the_matching_QR_link_on_the_clipboard(string platform, string expectedPrefix)
    {
        var copied = new List<string>();
        var localization = English();
        var viewModel = new MobileAppDownloadViewModel(new FakeMobileAppDownloadService(), localization, copied.Add);

        viewModel.CopyLinkCommand.Execute(platform);

        var link = Assert.Single(copied);
        Assert.StartsWith(expectedPrefix, link, StringComparison.Ordinal);
        Assert.Equal(localization.T("mobileApp.copied"), viewModel.CopyFeedbackText);
        Assert.True(viewModel.HasCopyFeedback);
    }

    [Fact]
    public void A_locked_clipboard_shows_a_failure_message_instead_of_crashing()
    {
        var localization = English();
        var viewModel = new MobileAppDownloadViewModel(
            new FakeMobileAppDownloadService(),
            localization,
            _ => throw new COMException("CLIPBRD_E_CANT_OPEN"));

        viewModel.CopyLinkCommand.Execute("ios");

        Assert.Equal(localization.T("mobileApp.copyFailed"), viewModel.CopyFeedbackText);
    }

    [Fact]
    public void Enlarging_shows_the_chosen_platform_QR_and_closing_hides_it()
    {
        var localization = English();
        var viewModel = new MobileAppDownloadViewModel(new FakeMobileAppDownloadService(), localization);

        Assert.False(viewModel.IsEnlarged);

        viewModel.EnlargeCommand.Execute("android");

        Assert.True(viewModel.IsEnlarged);
        Assert.Equal(viewModel.AndroidQrPayload, viewModel.EnlargedQrPayload);
        Assert.Equal(localization.T("mobileApp.android.title"), viewModel.EnlargedTitleText);

        viewModel.CloseEnlargedCommand.Execute(null);

        Assert.False(viewModel.IsEnlarged);
        Assert.Equal(string.Empty, viewModel.EnlargedQrPayload);

        viewModel.EnlargeCommand.Execute("ios");

        Assert.Equal(viewModel.IosQrPayload, viewModel.EnlargedQrPayload);
        Assert.Equal(localization.T("mobileApp.ios.title"), viewModel.EnlargedTitleText);
    }

    private static LocalizationService English()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        return localization;
    }
}

/// <summary>可控的假服务：测试用 <see cref="OnRefresh"/> 决定每次联网查询的结果与时机。</summary>
internal sealed class FakeMobileAppDownloadService : IMobileAppDownloadService
{
    private readonly MobileAppInstallInfo _local =
        MobileAppDownloadService.BuildLocalLinks(new Uri(MobileAppDownloadService.DefaultCenterBaseUrl));

    public Func<Task<MobileAppInstallInfo>>? OnRefresh { get; set; }

    public int RefreshCalls { get; private set; }

    public MobileAppInstallInfo GetLocalLinks() => _local;

    public Task<MobileAppInstallInfo> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return OnRefresh?.Invoke() ?? Task.FromResult(_local);
    }
}
