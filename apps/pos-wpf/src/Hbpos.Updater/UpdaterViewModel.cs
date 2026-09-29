using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Hbpos.Updater;

internal enum UpdaterStage
{
    Closing,
    Installing,
    Completed,
    Failed
}

internal enum UpdaterStepState
{
    Pending,
    Active,
    Done
}

internal enum UpdaterFailureKind
{
    InstallerFailed,
    InstallerCancelled,
    ElevationCancelled,
    RestartRequired,
    InstallerMissing,
    LaunchFailed,
    Unexpected
}

internal abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class UpdaterStepViewModel(string number, string label, UpdaterStepState state) : ObservableModel
{
    private UpdaterStepState _state = state;

    public string Number { get; } = number;

    public string Label { get; } = label;

    public UpdaterStepState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>
/// 更新窗口的显示状态；阶段切换由 UpdateSession 驱动，这里只负责把状态翻译成文案和样式开关。
/// </summary>
internal sealed class UpdaterViewModel : ObservableModel
{
    private static readonly TimeSpan MinimumElapsedForEstimate = TimeSpan.FromSeconds(3);
    private const int MinimumPercentForEstimate = 5;

    private readonly UpdaterStrings _strings;
    private UpdaterStage _stage;
    private InstallerProgressSnapshot? _progress;
    private string _title;
    private string _subtitle;
    private string _stageTitle;
    private string _stageDetail;
    private string _percentText;
    private bool _isPercentNumeric;
    private double _progressValue;
    private bool _isIndeterminate = true;
    private string _elapsedText = string.Empty;
    private string _hintText;
    private string _failureTitle = string.Empty;
    private string _failureMessage = string.Empty;
    private bool _canViewLog;

    public UpdaterViewModel(UpdaterStrings strings, string? fromVersion, string? toVersion)
    {
        _strings = strings;
        FromVersion = fromVersion ?? string.Empty;
        ToVersion = toVersion ?? string.Empty;
        DownloadStep = new UpdaterStepViewModel("1", strings.StepDownload, UpdaterStepState.Done);
        InstallStep = new UpdaterStepViewModel("2", strings.StepInstall, UpdaterStepState.Active);
        LaunchStep = new UpdaterStepViewModel("3", strings.StepLaunch, UpdaterStepState.Pending);
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke(this, EventArgs.Empty));
        OpenCurrentVersionCommand = new RelayCommand(() => OpenCurrentVersionRequested?.Invoke(this, EventArgs.Empty));
        ViewLogCommand = new RelayCommand(() => ViewLogRequested?.Invoke(this, EventArgs.Empty));
        _title = strings.UpdatingTitle;
        _subtitle = strings.UpdatingSubtitle;
        _stageTitle = strings.ClosingStageTitle;
        _stageDetail = strings.ClosingStageDetail;
        _percentText = strings.PreparingPercent;
        _hintText = strings.ClosingHint;
    }

    public event EventHandler? RetryRequested;

    public event EventHandler? OpenCurrentVersionRequested;

    public event EventHandler? ViewLogRequested;

    public string WindowTitle => _strings.WindowTitle;

    public string VersionLabel => _strings.VersionLabel;

    public string ViewLogText => _strings.ViewLogButton;

    public string OpenCurrentVersionText => _strings.OpenCurrentButton;

    public string RetryText => _strings.RetryButton;

    public string FromVersion { get; }

    public string ToVersion { get; }

    public bool HasVersions => FromVersion.Length > 0 && ToVersion.Length > 0;

    public UpdaterStepViewModel DownloadStep { get; }

    public UpdaterStepViewModel InstallStep { get; }

    public UpdaterStepViewModel LaunchStep { get; }

    public ICommand RetryCommand { get; }

    public ICommand OpenCurrentVersionCommand { get; }

    public ICommand ViewLogCommand { get; }

    public UpdaterStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsProgressVisible));
                OnPropertyChanged(nameof(CanClose));
            }
        }
    }

    public bool IsCompleted => Stage == UpdaterStage.Completed;

    public bool IsFailed => Stage == UpdaterStage.Failed;

    public bool IsProgressVisible => Stage != UpdaterStage.Failed;

    // 安装进行中关窗只会让收银员看不到进度，安装器仍在后台运行，所以只在结束后允许关闭。
    public bool CanClose => Stage is UpdaterStage.Completed or UpdaterStage.Failed;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string StageTitle
    {
        get => _stageTitle;
        private set => SetProperty(ref _stageTitle, value);
    }

    public string StageDetail
    {
        get => _stageDetail;
        private set => SetProperty(ref _stageDetail, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => SetProperty(ref _percentText, value);
    }

    public bool IsPercentNumeric
    {
        get => _isPercentNumeric;
        private set => SetProperty(ref _isPercentNumeric, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set
        {
            if (SetProperty(ref _isIndeterminate, value))
            {
                OnPropertyChanged(nameof(IsDeterminate));
            }
        }
    }

    public bool IsDeterminate => !IsIndeterminate;

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string HintText
    {
        get => _hintText;
        private set => SetProperty(ref _hintText, value);
    }

    public string FailureTitle
    {
        get => _failureTitle;
        private set => SetProperty(ref _failureTitle, value);
    }

    public string FailureMessage
    {
        get => _failureMessage;
        private set => SetProperty(ref _failureMessage, value);
    }

    public bool CanViewLog
    {
        get => _canViewLog;
        private set => SetProperty(ref _canViewLog, value);
    }

    public void ShowClosing()
    {
        Stage = UpdaterStage.Closing;
        _progress = null;
        ApplyUpdatingHeader();
        StageTitle = _strings.ClosingStageTitle;
        StageDetail = _strings.ClosingStageDetail;
        ShowPreparingProgress();
        SetSteps(UpdaterStepState.Active, UpdaterStepState.Pending);
        UpdateElapsed(TimeSpan.Zero);
    }

    public void ShowInstalling()
    {
        Stage = UpdaterStage.Installing;
        _progress = null;
        ApplyUpdatingHeader();
        StageTitle = _strings.InstallingStageTitle;
        StageDetail = _strings.InstallerStartingDetail;
        ShowPreparingProgress();
        SetSteps(UpdaterStepState.Active, UpdaterStepState.Pending);
        UpdateElapsed(TimeSpan.Zero);
    }

    public void ReportInstallerProgress(InstallerProgressSnapshot progress)
    {
        if (Stage != UpdaterStage.Installing)
        {
            return;
        }

        _progress = progress;
        switch (progress.Stage)
        {
            case InstallerProgressStage.Preparing:
                StageDetail = _strings.InstallerPreparingDetail;
                ShowPreparingProgress();
                break;
            case InstallerProgressStage.Installing:
                StageDetail = _strings.InstallingFilesDetail;
                ShowPercent(progress.Percent);
                break;
            case InstallerProgressStage.Finishing:
                StageDetail = _strings.FinishingDetail;
                ShowPercent(100);
                break;
        }
    }

    public void UpdateElapsed(TimeSpan elapsed)
    {
        switch (Stage)
        {
            case UpdaterStage.Closing:
                ElapsedText = Format(_strings.ElapsedFormat, FormatDuration(elapsed));
                HintText = _strings.ClosingHint;
                break;
            case UpdaterStage.Installing:
                ElapsedText = Format(_strings.ElapsedFormat, FormatDuration(elapsed));
                HintText = BuildInstallingHint(elapsed);
                break;
        }
    }

    public void ShowCompleted(TimeSpan totalDuration)
    {
        Stage = UpdaterStage.Completed;
        Title = _strings.CompletedTitle;
        Subtitle = _strings.CompletedSubtitle;
        StageTitle = _strings.CompletedStageTitle;
        StageDetail = ToVersion.Length > 0
            ? Format(_strings.OpeningVersionDetail, ToVersion)
            : _strings.OpeningDetail;
        ShowPercent(100);
        SetSteps(UpdaterStepState.Done, UpdaterStepState.Active);
        ElapsedText = Format(_strings.TotalElapsedFormat, FormatDuration(totalDuration));
        HintText = _strings.AutoCloseHint;
    }

    public void ShowFailed(UpdaterFailureKind kind, string? detail, bool canViewLog)
    {
        Stage = UpdaterStage.Failed;
        Title = _strings.FailedTitle;
        Subtitle = FromVersion.Length > 0
            ? Format(_strings.FailedSubtitleWithVersion, FromVersion)
            : _strings.FailedSubtitle;
        FailureTitle = ToVersion.Length > 0
            ? Format(_strings.FailureTitleWithVersion, ToVersion)
            : _strings.FailureTitle;
        FailureMessage = kind switch
        {
            UpdaterFailureKind.InstallerCancelled => _strings.InstallerCancelledMessage,
            UpdaterFailureKind.ElevationCancelled => _strings.ElevationCancelledMessage,
            UpdaterFailureKind.RestartRequired => _strings.RestartRequiredMessage,
            UpdaterFailureKind.InstallerMissing => _strings.InstallerMissingMessage,
            UpdaterFailureKind.LaunchFailed => Format(_strings.LaunchFailedMessage, detail ?? string.Empty),
            UpdaterFailureKind.Unexpected => Format(_strings.UnexpectedFailureMessage, detail ?? string.Empty),
            _ => Format(_strings.InstallerFailedMessage, detail ?? string.Empty)
        };
        CanViewLog = canViewLog;
    }

    public void ShowOpenAppFailed()
    {
        FailureMessage = _strings.OpenAppFailedMessage;
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (int)duration.TotalSeconds);
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private string BuildInstallingHint(TimeSpan elapsed)
    {
        switch (_progress)
        {
            case null:
                // 还没收到安装器进度时，多半停在 Windows 提权确认框上。
                return _strings.ElevationHint;
            case { Stage: InstallerProgressStage.Finishing }:
                return string.Empty;
            case { Stage: InstallerProgressStage.Installing, Percent: var percent }
                when percent >= MinimumPercentForEstimate && percent < 100 && elapsed >= MinimumElapsedForEstimate:
                var remainingSeconds = elapsed.TotalSeconds * (100 - percent) / percent;
                return remainingSeconds < 60
                    ? Format(_strings.RemainingSecondsFormat, Math.Max(1, (int)Math.Ceiling(remainingSeconds)))
                    : Format(_strings.RemainingMinutesFormat, (int)Math.Ceiling(remainingSeconds / 60));
            default:
                return _strings.EstimatingHint;
        }
    }

    private void ApplyUpdatingHeader()
    {
        Title = _strings.UpdatingTitle;
        Subtitle = _strings.UpdatingSubtitle;
        CanViewLog = false;
    }

    private void ShowPreparingProgress()
    {
        PercentText = _strings.PreparingPercent;
        IsPercentNumeric = false;
        ProgressValue = 0;
        IsIndeterminate = true;
    }

    private void ShowPercent(int percent)
    {
        PercentText = string.Create(CultureInfo.InvariantCulture, $"{percent}%");
        IsPercentNumeric = true;
        ProgressValue = percent;
        IsIndeterminate = false;
    }

    private void SetSteps(UpdaterStepState install, UpdaterStepState launch)
    {
        InstallStep.State = install;
        LaunchStep.State = launch;
    }

    private static string Format(string template, object value)
    {
        return string.Format(CultureInfo.CurrentCulture, template, value);
    }
}
