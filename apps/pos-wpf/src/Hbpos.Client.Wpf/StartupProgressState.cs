using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf;

public enum StartupStepState
{
    Pending,
    Active,
    Done
}

public sealed partial class StartupStepItem(StartupPhase phase, string title) : ObservableObject
{
    public StartupPhase Phase { get; } = phase;

    /// <summary>步骤清单上的短名称，按启动语言解析。</summary>
    public string Title { get; } = title;

    [ObservableProperty]
    private StartupStepState state;
}

/// <summary>
/// 启动页视图模型，只在启动页线程上读写。
/// 文案在创建时按启动语言一次性解析，启动过程中不跟随主线程切换语言，避免中途中英文闪变。
/// </summary>
public sealed partial class StartupProgressState : ObservableObject
{
    // 网络阶段超过"3 秒且超过 2 倍本机常见耗时"才提示服务器较慢，避免正常波动误报。
    private static readonly TimeSpan SlowHintMinimum = TimeSpan.FromSeconds(3);
    private const double SlowHintExpectedMultiplier = 2;

    private readonly Func<string, string> _localize;
    private readonly CultureInfo _culture;
    private double _displayedProgress;

    [ObservableProperty]
    private string titleText;

    [ObservableProperty]
    private string subtitleText;

    [ObservableProperty]
    private string stageTitle = string.Empty;

    [ObservableProperty]
    private string stageDetail = string.Empty;

    [ObservableProperty]
    private bool isStageSlow;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string percentText = "0";

    [ObservableProperty]
    private string stepText = string.Empty;

    [ObservableProperty]
    private string elapsedText = string.Empty;

    [ObservableProperty]
    private string versionLabel;

    [ObservableProperty]
    private string versionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotice))]
    private string updateNoticeText = string.Empty;

    public StartupProgressState(Func<string, string> localize, CultureInfo culture, string? storeLabel = null)
    {
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(culture);
        _localize = localize;
        _culture = culture;
        titleText = localize("startup.title");
        subtitleText = string.IsNullOrWhiteSpace(storeLabel) ? localize("startup.subtitle") : storeLabel.Trim();
        versionLabel = localize("startup.versionLabel");
        Steps = StartupProgressTracker.Phases
            .Select(phase => new StartupStepItem(phase, localize("startup.stepName." + phase.ToString().ToLowerInvariant())))
            .ToArray();
        ApplyStage(StartupPhase.Services, TimeSpan.Zero, TimeSpan.MaxValue);
        ElapsedText = FormatElapsed(TimeSpan.Zero);
    }

    public IReadOnlyList<StartupStepItem> Steps { get; }

    public bool HasUpdateNotice => !string.IsNullOrWhiteSpace(UpdateNoticeText);

    /// <summary>当前显示的整数百分比；未真正完成前最多显示 99。</summary>
    public int Percent { get; private set; }

    public void SetVersion(string version, AppLaunchVersionNotice? notice)
    {
        VersionText = version;
        UpdateNoticeText = notice is null
            ? string.Empty
            : string.Format(
                _culture,
                _localize(notice.IsRollback ? "startup.rolledBackTo" : "startup.updatedTo"),
                notice.Version);
    }

    /// <summary>每帧调用：平滑追赶追踪器给出的目标进度，并刷新阶段文案。</summary>
    public void Apply(StartupProgressSnapshot snapshot, TimeSpan frameInterval)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _displayedProgress = StartupProgressTracker.SmoothTowards(_displayedProgress, snapshot.Progress, frameInterval);
        Percent = snapshot.IsCompleted && _displayedProgress >= 1
            ? 100
            : Math.Min(99, (int)Math.Floor(_displayedProgress * 100));
        ProgressValue = _displayedProgress * 100;
        PercentText = Percent.ToString(_culture);
        ElapsedText = FormatElapsed(snapshot.Elapsed);

        if (snapshot.IsCompleted)
        {
            StageTitle = _localize("startup.phase.completed");
            StageDetail = string.Empty;
            IsStageSlow = false;
            UpdateSteps(Steps.Count);
            StepText = FormatStep(Steps.Count);
            return;
        }

        ApplyStage(
            snapshot.CurrentPhase ?? StartupPhase.Services,
            snapshot.CurrentPhaseElapsed,
            snapshot.CurrentPhaseExpected);
    }

    private void ApplyStage(StartupPhase phase, TimeSpan phaseElapsed, TimeSpan phaseExpected)
    {
        var key = "startup.phase." + phase.ToString().ToLowerInvariant();
        StageTitle = _localize(key);
        IsStageSlow = IsNetworkPhase(phase) &&
            phaseElapsed >= SlowHintMinimum &&
            phaseElapsed.TotalMilliseconds >= phaseExpected.TotalMilliseconds * SlowHintExpectedMultiplier;
        StageDetail = IsStageSlow
            ? string.Format(_culture, _localize("startup.slowNetwork"), (int)phaseElapsed.TotalSeconds)
            : _localize(key + ".detail");

        var index = (int)phase;
        UpdateSteps(index);
        StepText = FormatStep(index + 1);
    }

    private void UpdateSteps(int activeIndex)
    {
        for (var index = 0; index < Steps.Count; index++)
        {
            Steps[index].State = index < activeIndex
                ? StartupStepState.Done
                : index == activeIndex ? StartupStepState.Active : StartupStepState.Pending;
        }
    }

    private string FormatStep(int stepNumber) =>
        string.Format(_culture, _localize("startup.step"), stepNumber, Steps.Count);

    private string FormatElapsed(TimeSpan elapsed) =>
        string.Format(_culture, _localize("startup.elapsed"), elapsed.TotalSeconds.ToString("0.0", _culture));

    private static bool IsNetworkPhase(StartupPhase phase) =>
        phase is StartupPhase.Update or StartupPhase.Device;
}
