using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Wpf.Services;

public enum RestartPromptMode
{
    None,
    Prompt,
    Countdown,
    Blocked,
    Working,
    Failed
}

/// <summary>阻止界面里的一行问题。</summary>
public sealed record RestartIssueRow(string Text, bool IsHard);

/// <summary>
/// 「日结后重启」浮层的界面实现：只持有展示状态并把按钮点击转成任务结果，不含任何业务判断
/// （检查、倒计时、审计都在 <see cref="PostDailyCloseRestartCoordinator"/>）。
/// XAML 浮层（RestartPromptOverlay）直接绑定它的展示状态。与 <see cref="WpfConfirmationDialogService"/> 同样是单例：同一时刻只有一个流程，已有流程时新请求直接取消。
/// </summary>
public sealed class WpfRestartPromptService :
    ObservableObject,
    IRestartPromptUi,
    IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly object _sync = new();

    private RestartPromptMode _mode;
    private TaskCompletionSource<RestartPromptChoice>? _promptSource;
    private TaskCompletionSource<RestartBlockedChoice>? _blockedSource;
    private TaskCompletionSource<RestartCountdownAction>? _countdownSource;
    private TaskCompletionSource? _failureSource;
    private CancellationTokenRegistration _cancellationRegistration;

    // 用于文案刷新（切换语言时重算）。
    private RestartPromptInfo? _promptInfo;
    private RestartSafetyReport? _blockedReport;
    private RestartFailureInfo? _failureInfo;
    private int _countdownTotal;
    private int _countdownRemaining;
    private bool _canGoToPos;
    private bool _disposed;

    private string _titleText = string.Empty;
    private string _subtitleText = string.Empty;
    private string _bodyText = string.Empty;
    private string _noteText = string.Empty;
    private string _primaryText = string.Empty;
    private string _secondaryText = string.Empty;
    private string _tertiaryText = string.Empty;
    private string _uptimeFactText = string.Empty;
    private string _uploadsFactText = string.Empty;
    private string _uploadsFactTone = "Ok";
    private string _failureDetailText = string.Empty;

    public WpfRestartPromptService(ILocalizationService localization)
    {
        _localization = localization;
        PrimaryCommand = new RelayCommand(OnPrimary, () => IsOpen);
        SecondaryCommand = new RelayCommand(OnSecondary, () => IsOpen);
        TertiaryCommand = new RelayCommand(OnTertiary, () => IsOpen);
        _localization.CultureChanged += OnCultureChanged;
    }

    public ObservableCollection<RestartIssueRow> Issues { get; } = [];

    public IRelayCommand PrimaryCommand { get; }

    public IRelayCommand SecondaryCommand { get; }

    public IRelayCommand TertiaryCommand { get; }

    public RestartPromptMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(IsPromptMode));
            OnPropertyChanged(nameof(IsCountdownMode));
            OnPropertyChanged(nameof(IsBlockedMode));
            OnPropertyChanged(nameof(IsWorkingMode));
            OnPropertyChanged(nameof(IsFailedMode));
            OnPropertyChanged(nameof(IsWarningTone));
            OnPropertyChanged(nameof(IsDangerTone));
            OnPropertyChanged(nameof(HasSecondary));
            OnPropertyChanged(nameof(HasTertiary));
            OnPropertyChanged(nameof(HasPrimary));
            PrimaryCommand.NotifyCanExecuteChanged();
            SecondaryCommand.NotifyCanExecuteChanged();
            TertiaryCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsOpen => Mode != RestartPromptMode.None;

    public bool IsPromptMode => Mode == RestartPromptMode.Prompt;

    public bool IsCountdownMode => Mode == RestartPromptMode.Countdown;

    public bool IsBlockedMode => Mode == RestartPromptMode.Blocked;

    public bool IsWorkingMode => Mode == RestartPromptMode.Working;

    public bool IsFailedMode => Mode == RestartPromptMode.Failed;

    public bool IsWarningTone => Mode == RestartPromptMode.Blocked;

    public bool IsDangerTone => Mode == RestartPromptMode.Failed;

    public bool HasPrimary => Mode != RestartPromptMode.Working && Mode != RestartPromptMode.None;

    public bool HasSecondary => Mode is RestartPromptMode.Prompt or RestartPromptMode.Countdown or RestartPromptMode.Blocked;

    public bool HasTertiary => Mode == RestartPromptMode.Blocked && _canGoToPos;

    public string TitleText { get => _titleText; private set => SetProperty(ref _titleText, value); }

    public string SubtitleText { get => _subtitleText; private set => SetProperty(ref _subtitleText, value); }

    public string BodyText { get => _bodyText; private set => SetProperty(ref _bodyText, value); }

    public string NoteText { get => _noteText; private set => SetProperty(ref _noteText, value); }

    public string PrimaryText { get => _primaryText; private set => SetProperty(ref _primaryText, value); }

    public string SecondaryText { get => _secondaryText; private set => SetProperty(ref _secondaryText, value); }

    public string TertiaryText { get => _tertiaryText; private set => SetProperty(ref _tertiaryText, value); }

    public string UptimeFactText { get => _uptimeFactText; private set => SetProperty(ref _uptimeFactText, value); }

    public string UploadsFactText { get => _uploadsFactText; private set => SetProperty(ref _uploadsFactText, value); }

    /// <summary>"Ok" / "Warn"：积压为 0 时绿色，否则警告色。</summary>
    public string UploadsFactTone { get => _uploadsFactTone; private set => SetProperty(ref _uploadsFactTone, value); }

    public string FailureDetailText { get => _failureDetailText; private set => SetProperty(ref _failureDetailText, value); }

    public int CountdownRemaining => _countdownRemaining;

    public int CountdownTotal => _countdownTotal;

    /// <summary>倒计时环的进度 0~1（剩余比例）。</summary>
    public double CountdownProgress => _countdownTotal <= 0 ? 0 : Math.Clamp((double)_countdownRemaining / _countdownTotal, 0, 1);

    public string CountdownLabelText => _localization.T("restart.countdown.seconds");

    public string CountdownCheckCartText => _localization.T("restart.countdown.check.cart");

    public string CountdownCheckCardText => _localization.T("restart.countdown.check.card");

    public string CountdownCheckFinalText => _localization.T("restart.countdown.check.final");

    public string PromptFactUptimeLabel => _localization.T("restart.prompt.factUptime");

    public string PromptFactUploadsLabel => _localization.T("restart.prompt.factUploads");

    public string PromptFactCartLabel => _localization.T("restart.prompt.factCart");

    public string PromptFactCartText => _localization.T("restart.prompt.cartClear");

    public string BlockedTagText => _localization.T("restart.blocked.tagMust");

    public Task<RestartPromptChoice> ShowPromptAsync(RestartPromptInfo info, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<RestartPromptChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (!TryBegin(cancellationToken, () => source.TrySetCanceled(cancellationToken)))
            {
                // 浮层被别的流程占用（或已取消）：按取消处理，协调器会安静退出。
                return Task.FromCanceled<RestartPromptChoice>(new CancellationToken(true));
            }

            _promptSource = source;
            _promptInfo = info;
        }

        Mode = RestartPromptMode.Prompt;
        RefreshText();
        return source.Task;
    }

    public Task<RestartBlockedChoice> ShowBlockedAsync(
        RestartSafetyReport report,
        bool canGoToPos,
        CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<RestartBlockedChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ResetSources();
            _cancellationRegistration.Dispose();
            _cancellationRegistration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _blockedSource = source;
            _blockedReport = report;
            _canGoToPos = canGoToPos;
        }

        Mode = RestartPromptMode.Blocked;
        RefreshText();
        return source.Task;
    }

    public Task<RestartCountdownAction> ShowCountdown(int totalSeconds, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<RestartCountdownAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ResetSources();
            _cancellationRegistration.Dispose();
            _cancellationRegistration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _countdownSource = source;
            _countdownTotal = Math.Max(1, totalSeconds);
            _countdownRemaining = _countdownTotal;
        }

        Mode = RestartPromptMode.Countdown;
        RefreshText();
        RaiseCountdownChanged();
        return source.Task;
    }

    public void SetCountdownRemaining(int remainingSeconds)
    {
        _countdownRemaining = Math.Clamp(remainingSeconds, 0, Math.Max(_countdownTotal, 1));
        RaiseCountdownChanged();
    }

    public void ShowWorking()
    {
        lock (_sync)
        {
            ResetSources();
        }

        Mode = RestartPromptMode.Working;
        RefreshText();
    }

    public Task ShowFailureAsync(RestartFailureInfo info, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ResetSources();
            _cancellationRegistration.Dispose();
            _cancellationRegistration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _failureSource = source;
            _failureInfo = info;
        }

        Mode = RestartPromptMode.Failed;
        RefreshText();
        return source.Task;
    }

    public void Hide()
    {
        lock (_sync)
        {
            ResetSources();
            _cancellationRegistration.Dispose();
            _cancellationRegistration = default;
            _promptInfo = null;
            _blockedReport = null;
            _failureInfo = null;
            _canGoToPos = false;
        }

        Issues.Clear();
        Mode = RestartPromptMode.None;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.CultureChanged -= OnCultureChanged;
        _cancellationRegistration.Dispose();
    }

    private bool TryBegin(CancellationToken cancellationToken, Action onCancelled)
    {
        // 已有流程占用浮层时拒绝新流程（与确认弹窗一致，不排队）。
        if (_mode != RestartPromptMode.None || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        _cancellationRegistration.Dispose();
        _cancellationRegistration = cancellationToken.Register(onCancelled);
        return true;
    }

    /// <summary>切换模式前把上一步还没完成的任务按「取消」结束，避免协调器永远等一个已经不可见的界面。</summary>
    private void ResetSources()
    {
        _promptSource?.TrySetCanceled();
        _blockedSource?.TrySetCanceled();
        _countdownSource?.TrySetCanceled();
        _failureSource?.TrySetCanceled();
        _promptSource = null;
        _blockedSource = null;
        _countdownSource = null;
        _failureSource = null;
    }

    private void OnPrimary()
    {
        switch (Mode)
        {
            case RestartPromptMode.Prompt:
                Complete(ref _promptSource, RestartPromptChoice.RestartNow);
                break;
            case RestartPromptMode.Countdown:
                Complete(ref _countdownSource, RestartCountdownAction.RestartNow);
                break;
            case RestartPromptMode.Blocked:
                Complete(ref _blockedSource, RestartBlockedChoice.Recheck);
                break;
            case RestartPromptMode.Failed:
                CompleteFailure();
                break;
        }
    }

    private void OnSecondary()
    {
        switch (Mode)
        {
            case RestartPromptMode.Prompt:
                Complete(ref _promptSource, RestartPromptChoice.Later);
                break;
            case RestartPromptMode.Countdown:
                Complete(ref _countdownSource, RestartCountdownAction.Cancel);
                break;
            case RestartPromptMode.Blocked:
                Complete(ref _blockedSource, RestartBlockedChoice.Dismiss);
                break;
        }
    }

    private void OnTertiary()
    {
        if (Mode == RestartPromptMode.Blocked)
        {
            Complete(ref _blockedSource, RestartBlockedChoice.GoToPos);
        }
    }

    private void Complete<T>(ref TaskCompletionSource<T>? source, T result)
    {
        TaskCompletionSource<T>? taken;
        lock (_sync)
        {
            taken = source;
            source = null;
        }

        // 先取走再完成：重复点击只会命中一次结果。
        taken?.TrySetResult(result);
    }

    private void CompleteFailure()
    {
        TaskCompletionSource? taken;
        lock (_sync)
        {
            taken = _failureSource;
            _failureSource = null;
        }

        taken?.TrySetResult();
    }

    private void RaiseCountdownChanged()
    {
        OnPropertyChanged(nameof(CountdownRemaining));
        OnPropertyChanged(nameof(CountdownTotal));
        OnPropertyChanged(nameof(CountdownProgress));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (IsOpen)
        {
            RefreshText();
        }
    }

    private void RefreshText()
    {
        var culture = CultureInfo.CurrentCulture;
        switch (Mode)
        {
            case RestartPromptMode.Prompt when _promptInfo is { } info:
                TitleText = _localization.T("restart.prompt.title");
                SubtitleText = _localization.T("restart.prompt.subtitle");
                BodyText = string.Format(culture, _localization.T("restart.prompt.body"), FormatUptimeShort(info.Uptime));
                NoteText = _localization.T("restart.prompt.note");
                PrimaryText = _localization.T("restart.action.now");
                SecondaryText = _localization.T("restart.action.later");
                UptimeFactText = FormatUptime(info.Uptime);
                UploadsFactText = info.PendingUploadCount > 0
                    ? string.Format(culture, _localization.T("restart.prompt.uploadsPending"), info.PendingUploadCount)
                    : _localization.T("restart.prompt.uploadsDone");
                UploadsFactTone = info.PendingUploadCount > 0 ? "Warn" : "Ok";
                break;
            case RestartPromptMode.Countdown:
                TitleText = _localization.T("restart.countdown.title");
                SubtitleText = _localization.T("restart.countdown.subtitle");
                BodyText = _localization.T("restart.countdown.body");
                NoteText = string.Empty;
                PrimaryText = _localization.T("restart.action.now");
                SecondaryText = _localization.T("restart.countdown.cancel");
                OnPropertyChanged(nameof(CountdownLabelText));
                OnPropertyChanged(nameof(CountdownCheckCartText));
                OnPropertyChanged(nameof(CountdownCheckCardText));
                OnPropertyChanged(nameof(CountdownCheckFinalText));
                break;
            case RestartPromptMode.Blocked when _blockedReport is { } report:
                RefreshBlocked(report, culture);
                break;
            case RestartPromptMode.Working:
                TitleText = _localization.T("restart.working.title");
                SubtitleText = _localization.T("restart.working.subtitle");
                BodyText = _localization.T("restart.working.body");
                NoteText = string.Empty;
                break;
            case RestartPromptMode.Failed when _failureInfo is { } failure:
                TitleText = _localization.T("restart.failed.title");
                SubtitleText = _localization.T("restart.failed.subtitle");
                BodyText = _localization.T("restart.failed.body");
                NoteText = string.Empty;
                PrimaryText = _localization.T("restart.failed.ok");
                FailureDetailText = string.Format(
                    culture,
                    _localization.T("restart.failed.detail"),
                    failure.ExitCode,
                    FormatUptime(failure.Uptime),
                    failure.ReferenceCode);
                break;
        }

        OnPropertyChanged(nameof(PromptFactUptimeLabel));
        OnPropertyChanged(nameof(PromptFactUploadsLabel));
        OnPropertyChanged(nameof(PromptFactCartLabel));
        OnPropertyChanged(nameof(PromptFactCartText));
        OnPropertyChanged(nameof(BlockedTagText));
    }

    private void RefreshBlocked(RestartSafetyReport report, CultureInfo culture)
    {
        var hard = report.HardIssues;
        TitleText = _localization.T("restart.blocked.title");
        SubtitleText = string.Format(culture, _localization.T("restart.blocked.subtitle"), hard.Count);
        BodyText = string.Empty;
        NoteText = _localization.T("restart.blocked.note");
        PrimaryText = _localization.T("restart.blocked.recheck");
        SecondaryText = _localization.T("restart.blocked.dismiss");
        TertiaryText = _localization.T("restart.blocked.goPos");

        Issues.Clear();
        foreach (var issue in hard)
        {
            Issues.Add(new RestartIssueRow(DescribeIssue(issue, culture), IsHard: true));
        }

        OnPropertyChanged(nameof(HasTertiary));
    }

    private string DescribeIssue(RestartSafetyIssue issue, CultureInfo culture) => issue.Code switch
    {
        RestartSafetyIssueCode.CartNotEmpty => string.Format(culture, _localization.T("restart.issue.cart"), issue.Count),
        RestartSafetyIssueCode.PaymentActive => _localization.T("restart.issue.payment"),
        RestartSafetyIssueCode.CardTerminalBusy => _localization.T("restart.issue.cardTerminal"),
        RestartSafetyIssueCode.CardRecoveryOpen => string.Format(culture, _localization.T("restart.issue.cardRecovery"), issue.Count),
        RestartSafetyIssueCode.LinklySettlementUnresolved => _localization.T("restart.issue.settlement"),
        RestartSafetyIssueCode.DailyCloseBusy => _localization.T("restart.issue.dailyClose"),
        RestartSafetyIssueCode.AppUpdateActive => _localization.T("restart.issue.update"),
        _ => _localization.T("restart.issue.checkFailed")
    };

    // 「9 天 3 小时」：界面展示用；不足一天时只显示天数 0 与小时，由调用方保证至少达到阈值，所以正常不会出现。
    private string FormatUptime(TimeSpan uptime) => string.Format(
        CultureInfo.CurrentCulture,
        _localization.T("restart.uptime.daysHours"),
        (int)uptime.TotalDays,
        uptime.Hours);

    private string FormatUptimeShort(TimeSpan uptime) => string.Format(
        CultureInfo.CurrentCulture,
        _localization.T("restart.uptime.days"),
        (int)uptime.TotalDays);
}
