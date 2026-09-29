using System.Windows;
using System.Windows.Markup;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Windows;
using Hbpos.Contracts.AppUpdates;

namespace Hbpos.Client.Wpf.Services;

public interface IAppUpdatePromptService
{
    Task<bool> ConfirmOptionalDownloadAndInstallAsync(
        AppUpdateCheckResponse update,
        CancellationToken cancellationToken = default);
}

public sealed class WpfAppUpdatePromptService : IAppUpdatePromptService
{
    private readonly ILocalizationService? _localization;
    private readonly IAppUpdatePromptDialogPresenter _dialogPresenter;

    public WpfAppUpdatePromptService(ILocalizationService? localization = null)
        : this(localization, new WpfAppUpdatePromptDialogPresenter())
    {
    }

    internal WpfAppUpdatePromptService(
        ILocalizationService? localization,
        IAppUpdatePromptDialogPresenter dialogPresenter)
    {
        _localization = localization;
        _dialogPresenter = dialogPresenter;
    }

    public Task<bool> ConfirmOptionalDownloadAndInstallAsync(
        AppUpdateCheckResponse update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var owner = Application.Current?.MainWindow;
        var culture = _localization?.CurrentCulture ?? LocalizationResourceProvider.Instance.CurrentCulture;
        var language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        var viewModel = new AppUpdatePromptViewModel(update);
        var result = _dialogPresenter.Show(viewModel, owner, language);

        return Task.FromResult(result == true);
    }
}

internal interface IAppUpdatePromptDialogPresenter
{
    bool? Show(AppUpdatePromptViewModel viewModel, Window? owner, XmlLanguage language);
}

internal readonly record struct AppUpdatePromptPlacement(
    WindowStartupLocation StartupLocation,
    Rect Bounds);

internal sealed class WpfAppUpdatePromptDialogPresenter : IAppUpdatePromptDialogPresenter
{
    private const double FallbackWidth = 1200;
    private const double FallbackHeight = 760;

    public bool? Show(AppUpdatePromptViewModel viewModel, Window? owner, XmlLanguage language)
    {
        // 中文注释：启动检查时 Application.MainWindow 还是 460×380 的置顶启动页，不能按它的尺寸压缩弹窗。
        var cashierWindowSize = owner is MainWindow { IsVisible: true }
            ? new Size(owner.ActualWidth, owner.ActualHeight)
            : (Size?)null;
        var placement = ResolvePlacement(cashierWindowSize, SystemParameters.WorkArea);
        var dialog = new AppUpdatePromptWindow(viewModel)
        {
            Language = language,
            WindowStartupLocation = placement.StartupLocation,
            Width = placement.Bounds.Width,
            Height = placement.Bounds.Height
        };

        if (placement.StartupLocation == WindowStartupLocation.Manual)
        {
            dialog.Left = placement.Bounds.Left;
            dialog.Top = placement.Bounds.Top;
        }

        if (owner is not null)
        {
            // 中文注释：启动页同样保留为 owner，弹窗才能压在置顶启动页之上。
            dialog.Owner = owner;
        }

        return dialog.ShowDialog();
    }

    internal static AppUpdatePromptPlacement ResolvePlacement(Size? cashierWindowSize, Rect workArea)
    {
        if (cashierWindowSize is { } size)
        {
            // 中文注释：弹窗窗口与主窗口同尺寸，确保遮罩覆盖完整收银界面。
            return new AppUpdatePromptPlacement(
                WindowStartupLocation.CenterOwner,
                new Rect(
                    0,
                    0,
                    ResolveOverlayLength(size.Width, FallbackWidth),
                    ResolveOverlayLength(size.Height, FallbackHeight)));
        }

        // 中文注释：收银主窗口尚未显示时遮罩铺满主屏工作区，与启动页同屏。
        return double.IsFinite(workArea.Width) && workArea.Width > 0 &&
            double.IsFinite(workArea.Height) && workArea.Height > 0
            ? new AppUpdatePromptPlacement(WindowStartupLocation.Manual, workArea)
            : new AppUpdatePromptPlacement(
                WindowStartupLocation.CenterScreen,
                new Rect(0, 0, FallbackWidth, FallbackHeight));
    }

    private static double ResolveOverlayLength(double actualLength, double fallbackLength)
    {
        return double.IsFinite(actualLength) && actualLength > 0
            ? actualLength
            : fallbackLength;
    }
}
