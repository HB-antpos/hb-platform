using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>一行检测结果：状态键固定为 ok / slow / failed / skipped，界面据此上色。</summary>
public sealed record DiagnosticStepItem(string Label, string Detail, string TimeText, string StatusKey);

/// <summary>
/// 「服务器地址」面板里的网络检测与一键修复。检测只读；修复必须先展示"要改什么"，用户确认后才执行，
/// 执行后自动复测，并且可以还原。
/// </summary>
public sealed partial class NetworkDiagnosticsViewModel : ObservableObject
{
    private readonly INetworkDiagnosticsService _diagnostics;
    private readonly INetworkFixService _fixes;
    private readonly ILocalizationService _localization;
    private readonly Func<string> _getAddressText;
    private readonly Action<string> _copyToClipboard;
    private NetworkDiagnosisReport? _report;
    private NetworkFixPlan? _plan;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _findingTitle = string.Empty;

    [ObservableProperty]
    private string _findingMessage = string.Empty;

    /// <summary>ok（无问题）/ warn（找到原因）。界面据此选卡片颜色。</summary>
    [ObservableProperty]
    private string _findingSeverity = "ok";

    [ObservableProperty]
    private bool _hasFix;

    [ObservableProperty]
    private string _fixButtonText = string.Empty;

    [ObservableProperty]
    private bool _isConfirming;

    [ObservableProperty]
    private string _planTarget = string.Empty;

    [ObservableProperty]
    private string _planBefore = string.Empty;

    [ObservableProperty]
    private string _planAfter = string.Empty;

    [ObservableProperty]
    private string _planNote = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string _resultSeverity = "ok";

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private string _appliedSummary = string.Empty;

    [ObservableProperty]
    private string _copyResultMessage = string.Empty;

    public NetworkDiagnosticsViewModel(
        INetworkDiagnosticsService diagnostics,
        INetworkFixService fixes,
        ILocalizationService localization,
        Func<string> getAddressText,
        Action<string>? copyToClipboard = null)
    {
        _diagnostics = diagnostics;
        _fixes = fixes;
        _localization = localization;
        _getAddressText = getAddressText;
        _copyToClipboard = copyToClipboard ?? CopyToSystemClipboard;
        DiagnoseCommand = new AsyncRelayCommand(DiagnoseAsync, () => !IsRunning);
        FixCommand = new RelayCommand(BeginFix, () => HasFix && !IsRunning && !IsConfirming);
        ConfirmFixCommand = new AsyncRelayCommand(ApplyFixAsync, () => IsConfirming && !IsRunning);
        CancelFixCommand = new RelayCommand(CancelFix, () => IsConfirming && !IsRunning);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo && !IsRunning);
        CopyReportCommand = new RelayCommand(CopyReport, () => _report is not null);
        RefreshUndoState();
    }

    public ObservableCollection<DiagnosticStepItem> Steps { get; } = [];

    public IAsyncRelayCommand DiagnoseCommand { get; }

    public IRelayCommand FixCommand { get; }

    public IAsyncRelayCommand ConfirmFixCommand { get; }

    public IRelayCommand CancelFixCommand { get; }

    public IAsyncRelayCommand UndoCommand { get; }

    public IRelayCommand CopyReportCommand { get; }

    public bool HasResultMessage => !string.IsNullOrEmpty(ResultMessage);

    public bool HasCopyResult => !string.IsNullOrEmpty(CopyResultMessage);

    public bool HasApplied => !string.IsNullOrEmpty(AppliedSummary);

    partial void OnIsRunningChanged(bool value) => RaiseCommandStates();

    partial void OnHasFixChanged(bool value) => RaiseCommandStates();

    partial void OnIsConfirmingChanged(bool value) => RaiseCommandStates();

    partial void OnCanUndoChanged(bool value) => RaiseCommandStates();

    partial void OnResultMessageChanged(string value) => OnPropertyChanged(nameof(HasResultMessage));

    partial void OnCopyResultMessageChanged(string value) => OnPropertyChanged(nameof(HasCopyResult));

    partial void OnAppliedSummaryChanged(string value) => OnPropertyChanged(nameof(HasApplied));

    private async Task DiagnoseAsync(CancellationToken cancellationToken)
    {
        Uri address;
        try
        {
            address = new Uri(ApiServerSettingsService.NormalizeAddress(_getAddressText()), UriKind.Absolute);
        }
        catch (ArgumentException)
        {
            ResultMessage = T("settings.serverAddress.status.invalid");
            ResultSeverity = "warn";
            return;
        }

        IsRunning = true;
        ResultMessage = string.Empty;
        CopyResultMessage = string.Empty;
        CancelFix();
        try
        {
            SummaryText = T("settings.network.running");
            var report = await _diagnostics.RunAsync(address, cancellationToken);
            Show(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消（离开页面）：保持现有显示。
        }
        catch (Exception ex)
        {
            // 检测本身出错不能让页面崩，也不要假装有结论。
            ConsoleLog.WriteWarning("NetworkDiagnostics", $"diagnosis failed error={ex.GetType().Name}: {ex.Message}");
            ResultMessage = T("settings.network.error");
            ResultSeverity = "warn";
            SummaryText = string.Empty;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Show(NetworkDiagnosisReport report)
    {
        _report = report;
        Steps.Clear();
        foreach (var step in report.Steps)
        {
            Steps.Add(new DiagnosticStepItem(
                T($"settings.network.step.{Camel(step.Id.ToString())}"),
                step.Detail,
                TimeText(step),
                StatusKey(step.Status)));
        }

        var kind = Camel(report.Finding.Kind.ToString());
        FindingTitle = T($"settings.network.finding.{kind}.title");
        FindingMessage = T($"settings.network.finding.{kind}.message");
        FindingSeverity = report.Finding.Kind == DiagnosisKind.None ? "ok" : "warn";
        HasFix = report.HasSuggestedFix;
        FixButtonText = report.Finding.SuggestedFix is { } fix ? T($"settings.network.fix.{Camel(fix.ToString())}") : string.Empty;
        SummaryText = string.Format(
            _localization.CurrentCulture,
            T("settings.network.finishedIn"),
            NetworkDiagnosisReport.FormatMs((long)report.Total.TotalMilliseconds));
        HasResult = true;
        CopyReportCommand.NotifyCanExecuteChanged();
        RefreshUndoState();
    }

    private void BeginFix()
    {
        var kind = _report?.Finding.SuggestedFix;
        if (kind is null)
        {
            return;
        }

        var plan = _fixes.Plan(kind.Value);
        if (plan is null)
        {
            ResultMessage = T("settings.network.fixUnavailable");
            ResultSeverity = "warn";
            return;
        }

        _plan = plan;
        PlanTarget = plan.Target;
        PlanBefore = plan.Before;
        PlanAfter = plan.After;
        PlanNote = T(plan.NeedsAdministrator ? "settings.network.confirm.admin" : "settings.network.confirm.currentUser");
        ResultMessage = string.Empty;
        IsConfirming = true;
    }

    private void CancelFix()
    {
        _plan = null;
        IsConfirming = false;
    }

    private async Task ApplyFixAsync(CancellationToken cancellationToken)
    {
        var plan = _plan;
        if (plan is null)
        {
            return;
        }

        IsRunning = true;
        try
        {
            var result = await _fixes.ApplyAsync(plan, cancellationToken);
            IsConfirming = false;
            _plan = null;
            if (result.Cancelled)
            {
                ResultMessage = T("settings.network.fixed.cancelled");
                ResultSeverity = "warn";
                return;
            }

            if (!result.Succeeded)
            {
                ResultMessage = $"{T("settings.network.fixed.failed")} {result.Error}".Trim();
                ResultSeverity = "warn";
                return;
            }

            // 改完立刻复测：用实测结果说明有没有好转，而不是只说"已修改"。
            var report = await _diagnostics.RunAsync(
                new Uri(ApiServerSettingsService.NormalizeAddress(_getAddressText()), UriKind.Absolute),
                cancellationToken);
            Show(report);
            var passed = report.Finding.Kind == DiagnosisKind.None;
            ResultMessage = T(passed ? "settings.network.fixed.passed" : "settings.network.fixed.stillFailing");
            ResultSeverity = passed ? "ok" : "warn";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：改动（如已执行）已记录，可从界面还原。
        }
        finally
        {
            IsRunning = false;
            RefreshUndoState();
        }
    }

    private async Task UndoAsync(CancellationToken cancellationToken)
    {
        var applied = Enum.GetValues<NetworkFixKind>().Select(kind => _fixes.GetApplied(kind)).Where(record => record is not null).ToArray();
        if (applied.Length == 0)
        {
            RefreshUndoState();
            return;
        }

        IsRunning = true;
        try
        {
            var allDone = true;
            foreach (var record in applied)
            {
                var result = await _fixes.UndoAsync(record!.Kind, cancellationToken);
                if (!result.Succeeded)
                {
                    allDone = false;
                    ResultMessage = result.Cancelled
                        ? T("settings.network.fixed.cancelled")
                        : $"{T("settings.network.fixed.failed")} {result.Error}".Trim();
                    ResultSeverity = "warn";
                    break;
                }
            }

            if (allDone)
            {
                ResultMessage = T("settings.network.undone");
                ResultSeverity = "ok";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：已还原的部分保持，剩余记录仍可再次还原。
        }
        finally
        {
            IsRunning = false;
            RefreshUndoState();
        }
    }

    private void CopyReport()
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            _copyToClipboard(_report.ToReportText());
            CopyResultMessage = T("settings.network.copied");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // 剪贴板被其他程序占用时给出提示，不让界面异常。
            CopyResultMessage = T("settings.network.copyFailed");
        }
    }

    private void RefreshUndoState()
    {
        var lines = new List<string>();
        foreach (var kind in Enum.GetValues<NetworkFixKind>())
        {
            if (_fixes.GetApplied(kind) is not { } record)
            {
                continue;
            }

            lines.Add(kind switch
            {
                NetworkFixKind.PublicDns => string.Format(
                    CultureInfo.InvariantCulture,
                    "DNS on {0}: {1} → {2}  ({3:yyyy-MM-dd HH:mm})",
                    record.AdapterName ?? "adapter",
                    record.PreviousAutomatic || record.PreviousServers.Count == 0
                        ? "automatic"
                        : string.Join(", ", record.PreviousServers),
                    string.Join(", ", NetworkFixService.PublicDnsServers),
                    record.AppliedAt),
                _ => string.Format(
                    CultureInfo.InvariantCulture,
                    "Proxy automatic detection: {0} → off  ({1:yyyy-MM-dd HH:mm})",
                    record.PreviousAutoDetect == 0 ? "off" : "on",
                    record.AppliedAt)
            });
        }

        AppliedSummary = string.Join(Environment.NewLine, lines);
        CanUndo = lines.Count > 0;
    }

    private void RaiseCommandStates()
    {
        DiagnoseCommand.NotifyCanExecuteChanged();
        FixCommand.NotifyCanExecuteChanged();
        ConfirmFixCommand.NotifyCanExecuteChanged();
        CancelFixCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private string T(string key) => _localization.T(key);

    private string TimeText(DiagnosticStep step)
    {
        return step.Status == DiagnosticStepStatus.Skipped
            ? T("settings.network.skipped")
            : step.ElapsedMs is { } ms
                ? NetworkDiagnosisReport.FormatMs(ms)
                : T("settings.network.ok");
    }

    private static string StatusKey(DiagnosticStepStatus status) => status switch
    {
        DiagnosticStepStatus.Ok => "ok",
        DiagnosticStepStatus.Slow => "slow",
        DiagnosticStepStatus.Failed => "failed",
        _ => "skipped"
    };

    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static void CopyToSystemClipboard(string text) => Clipboard.SetText(text);
}
