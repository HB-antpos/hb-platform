using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 收银员首次登录的配色选择窗：登录后由 <see cref="HandleCashierLoginAsync"/> 决定直接套用员工配色还是弹窗。
/// 窗内点卡片只预览不保存；「开始收银」才保存为本机配色与员工偏好。窗口没有关闭按钮，确认即视为选定当前预选项，所以只弹一次。
/// </summary>
public sealed partial class ColorThemeOnboardingViewModel : ObservableObject
{
    private readonly CashierColorThemePreferenceService _preferences;
    private readonly IColorThemeService _colorThemes;
    private readonly ICashierSessionContext _cashierSessions;
    private readonly ILocalizationService _localization;
    private CashierSessionDto? _session;
    private PosColorTheme _themeBeforePreview;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private PosColorTheme _selectedTheme;

    public ColorThemeOnboardingViewModel(
        CashierColorThemePreferenceService preferences,
        IColorThemeService colorThemes,
        ICashierSessionContext cashierSessions,
        ILocalizationService localization)
    {
        _preferences = preferences;
        _colorThemes = colorThemes;
        _cashierSessions = cashierSessions;
        _localization = localization;
        Options = new ObservableCollection<ColorThemeOption>(
            ColorThemeSwitcherViewModel.Definitions.Select(definition => new ColorThemeOption(definition.Theme, definition.Swatches)));
        PreviewCommand = new RelayCommand<PosColorTheme>(Preview);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync);
        _localization.CultureChanged += (_, _) => RefreshText();
        RefreshText();
    }

    public ObservableCollection<ColorThemeOption> Options { get; }

    public IRelayCommand<PosColorTheme> PreviewCommand { get; }

    public IAsyncRelayCommand ConfirmCommand { get; }

    public string Title { get; private set; } = string.Empty;

    public string Subtitle { get; private set; } = string.Empty;

    public string Hint { get; private set; } = string.Empty;

    public string ConfirmText { get; private set; } = string.Empty;

    public string AvatarText { get; private set; } = string.Empty;

    /// <summary>登录成功后调用：上一位收银员没确认的窗先撤回，再按新员工的偏好套用或弹窗。</summary>
    public async Task HandleCashierLoginAsync(CashierSessionDto session)
    {
        Abandon();
        CashierColorThemeOutcome outcome;
        try
        {
            outcome = await _preferences.ApplyForCashierAsync(session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 配色只是体验项，任何异常都不能影响收银员登录。
            ConsoleLog.WriteError("ColorTheme", $"cashier theme resolve failed error={ex.GetType().Name}", exception: ex);
            return;
        }

        ConsoleLog.Write("ColorTheme", $"cashier theme outcome={outcome}");
        if (outcome == CashierColorThemeOutcome.NeedsChoice && IsCurrent(session))
        {
            Open(session);
        }
    }

    private void Open(CashierSessionDto session)
    {
        _session = session;
        _themeBeforePreview = _colorThemes.Current;
        // 预选本机上次使用的配色（从未选过时即默认蓝色）。
        SelectedTheme = _colorThemes.Current;
        // 预选值与上次相同时属性不会变更，这里显式刷新勾选状态。
        OnSelectedThemeChanged(SelectedTheme);
        RefreshText();
        IsOpen = true;
    }

    private void Preview(PosColorTheme theme)
    {
        if (!IsOpen)
        {
            return;
        }

        SelectedTheme = theme;
        _colorThemes.Preview(theme);
    }

    private async Task ConfirmAsync()
    {
        if (!IsOpen || _session is not { } session)
        {
            return;
        }

        IsOpen = false;
        _session = null;
        if (!IsCurrent(session))
        {
            // 确认前已换人：不能把上一位员工的选择存到新会话名下。
            _colorThemes.Preview(_themeBeforePreview);
            return;
        }

        try
        {
            await _preferences.SaveChoiceAsync(session, SelectedTheme);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError("ColorTheme", $"cashier theme save failed error={ex.GetType().Name}", exception: ex);
        }
    }

    private void Abandon()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _session = null;
        _colorThemes.Preview(_themeBeforePreview);
    }

    private bool IsCurrent(CashierSessionDto session) =>
        string.Equals(_cashierSessions.CurrentSession?.UserGuid, session.UserGuid, StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedThemeChanged(PosColorTheme value)
    {
        foreach (var option in Options)
        {
            option.IsSelected = option.Theme == value;
        }

        RefreshConfirmText();
    }

    private void RefreshText()
    {
        foreach (var option in Options)
        {
            var key = "shell.colorTheme." + option.Theme.ToString().ToLowerInvariant();
            option.Name = _localization.T(key);
            option.Hint = _localization.T(key + ".hint");
        }

        var name = _session?.CashierName?.Trim() ?? string.Empty;
        Title = string.Format(_localization.CurrentCulture, _localization.T("shell.colorThemeOnboarding.title"), name);
        AvatarText = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : string.Empty;
        Subtitle = _localization.T("shell.colorThemeOnboarding.subtitle");
        Hint = _localization.T("shell.colorThemeOnboarding.hint");
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Hint));
        RefreshConfirmText();
    }

    private void RefreshConfirmText()
    {
        var themeName = Options.FirstOrDefault(option => option.Theme == SelectedTheme)?.Name ?? string.Empty;
        ConfirmText = string.Format(_localization.CurrentCulture, _localization.T("shell.colorThemeOnboarding.confirm"), themeName);
        OnPropertyChanged(nameof(ConfirmText));
    }
}
