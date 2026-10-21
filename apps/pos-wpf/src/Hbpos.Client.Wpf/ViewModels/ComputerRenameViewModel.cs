using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>「一键改电脑名」面板所处的步骤。</summary>
public enum ComputerRenameStep
{
    /// <summary>没有发现硬件冲突，面板不显示。</summary>
    Hidden,

    /// <summary>发现硬件冲突，提示可以改名，等用户点按钮。</summary>
    Offer,

    /// <summary>已给出新名字，等用户二次确认。</summary>
    Confirm,

    /// <summary>已改名，等重启后新电脑名（和硬件码）才生效。</summary>
    RestartPending,

    /// <summary>加入了域，不能在这里改名，只提示联系 IT。</summary>
    Unavailable
}

/// <summary>
/// 激活页发现「设备硬件码已被登记」（常见原因：两台电脑由同一个系统镜像克隆）时，提供一键改电脑名：
/// 取新名字 → 页内二次确认 → 管理员授权后改名 → 提示并执行重启。重启后硬件码变化，才能用开通码激活。
/// 状态集中在这个独立视图模型里，注册页本身只负责在合适的时机调用 <see cref="ShowForHardwareConflict"/> / <see cref="Reset"/>。
/// </summary>
public sealed partial class ComputerRenameViewModel : ObservableObject
{
    private readonly IComputerRenameService _service;
    private readonly Func<string, string, string> _translate;
    private readonly Func<string?> _targetStoreCode;
    private string _currentName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    [NotifyPropertyChangedFor(nameof(MessageText))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    [NotifyPropertyChangedFor(nameof(IsCancelVisible))]
    private ComputerRenameStep _step;

    /// <summary>Confirm 步骤里要改成的新名字；RestartPending 步骤里是已经改好的名字。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageText))]
    private string _proposedName = string.Empty;

    /// <summary>上一步的结果说明（用户拒绝授权、改名失败、正在重启），显示在面板正文下面。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunPrimary))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunPrimary))]
    private bool _isRestartRequested;

    /// <param name="service">改名与重启的实现；测试里传假实现。</param>
    /// <param name="translate">本地化函数（资源键, 英文兜底）→ 文案。</param>
    /// <param name="targetStoreCode">当前要激活的目标店号，用来给新电脑名带上店号；取不到时用不带店号的名字。</param>
    public ComputerRenameViewModel(
        IComputerRenameService service,
        Func<string, string, string> translate,
        Func<string?> targetStoreCode)
    {
        _service = service;
        _translate = translate;
        _targetStoreCode = targetStoreCode;
        PrimaryCommand = new AsyncRelayCommand(RunPrimaryAsync, () => CanRunPrimary);
        CancelCommand = new RelayCommand(CancelConfirmation, () => Step == ComputerRenameStep.Confirm && !IsBusy);
    }

    public IAsyncRelayCommand PrimaryCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public bool IsVisible => Step != ComputerRenameStep.Hidden;

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    /// <summary>加入了域时没有任何可点的按钮，只显示说明。</summary>
    public bool HasActions => Step is ComputerRenameStep.Offer or ComputerRenameStep.Confirm or ComputerRenameStep.RestartPending;

    public bool IsCancelVisible => Step == ComputerRenameStep.Confirm;

    public bool CanRunPrimary => HasActions && !IsBusy && !IsRestartRequested;

    public string TitleText => T("deviceActivation.rename.title", "Hardware ID conflict");

    public string MessageText => Step switch
    {
        ComputerRenameStep.Offer => T(
            "deviceActivation.rename.offer",
            "Another computer already uses this computer's hardware ID, usually because both were set up from the same system image. Renaming this computer gives it its own ID. The other computer is not changed."),
        ComputerRenameStep.Confirm => Format(
            T(
                "deviceActivation.rename.confirm",
                "Rename this computer from {0} to {1}? Windows will ask for administrator approval, and the computer must restart afterwards. The other computer is not changed."),
            _currentName,
            ProposedName),
        ComputerRenameStep.RestartPending => Format(
            T(
                "deviceActivation.rename.restartPending",
                "This computer is renamed to {0}. Restart it to finish, then enter the activation code again."),
            ProposedName),
        ComputerRenameStep.Unavailable => T(
            "deviceActivation.rename.unavailable",
            "This computer is joined to a domain, so it cannot be renamed here. Ask your IT administrator to rename it, then activate again."),
        _ => string.Empty
    };

    public string PrimaryButtonText => Step switch
    {
        ComputerRenameStep.Confirm => T("deviceActivation.rename.button.confirm", "Confirm rename"),
        ComputerRenameStep.RestartPending => T("deviceActivation.rename.button.restart", "Restart now"),
        _ => T("deviceActivation.rename.button.rename", "Rename this computer...")
    };

    public string CancelButtonText => T("deviceActivation.rename.button.cancel", "Cancel");

    /// <summary>服务端返回「设备已登记」时调用：读取本机状态，决定显示哪一步。</summary>
    public void ShowForHardwareConflict()
    {
        Notice = string.Empty;
        IsRestartRequested = false;
        ComputerRenameState state;
        try
        {
            state = _service.GetState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到电脑名状态也要给出改名入口；真正改名时若有问题会有明确的失败提示。
            state = new ComputerRenameState(Environment.MachineName, null, false);
        }

        _currentName = state.CurrentName;
        if (state.PendingName is not null)
        {
            // 之前已经改过名但还没重启：不要再改一次，直接提示重启。
            ProposedName = state.PendingName;
            Step = ComputerRenameStep.RestartPending;
        }
        else
        {
            Step = state.IsDomainJoined ? ComputerRenameStep.Unavailable : ComputerRenameStep.Offer;
        }

        NotifyCommandState();
    }

    /// <summary>
    /// 其他类型的失败（或重新开始）时调用：收起面板。已经改名等待重启的除外，
    /// 那个状态只在重启后才会结束，不能因为又点了一次激活而丢掉「请重启」的提示。
    /// </summary>
    public void Reset()
    {
        if (Step == ComputerRenameStep.RestartPending)
        {
            return;
        }

        Step = ComputerRenameStep.Hidden;
        Notice = string.Empty;
        NotifyCommandState();
    }

    /// <summary>界面语言切换后刷新所有文案。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(CancelButtonText));
    }

    private async Task RunPrimaryAsync()
    {
        switch (Step)
        {
            case ComputerRenameStep.Offer:
                Notice = string.Empty;
                ProposedName = ComputerNamePolicy.Generate(_targetStoreCode(), _currentName);
                Step = ComputerRenameStep.Confirm;
                break;

            case ComputerRenameStep.Confirm:
                await RenameAsync();
                break;

            case ComputerRenameStep.RestartPending:
                RestartNow();
                break;
        }

        NotifyCommandState();
    }

    private async Task RenameAsync()
    {
        IsBusy = true;
        NotifyCommandState();
        try
        {
            var renamed = await _service.RenameAsync(ProposedName, CancellationToken.None);
            if (renamed)
            {
                Notice = string.Empty;
                Step = ComputerRenameStep.RestartPending;
            }
            else
            {
                // 用户在 Windows 授权窗口点了「否」：什么都没改，回到提示步骤，可以重新来。
                Notice = T("deviceActivation.rename.notice.declined", "The rename was cancelled. Nothing was changed.");
                Step = ComputerRenameStep.Offer;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice = Format(
                T("deviceActivation.rename.notice.failed", "Could not rename the computer: {0}"),
                ex.Message);
            Step = ComputerRenameStep.Offer;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RestartNow()
    {
        try
        {
            _service.Restart();
            IsRestartRequested = true;
            Notice = T(
                "deviceActivation.rename.notice.restarting",
                "Restarting shortly. After the restart, open HB POS and enter the activation code again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice = Format(
                T("deviceActivation.rename.notice.restartFailed", "Could not restart the computer: {0}"),
                ex.Message);
        }
    }

    private void CancelConfirmation()
    {
        Notice = string.Empty;
        Step = ComputerRenameStep.Offer;
        NotifyCommandState();
    }

    private void NotifyCommandState()
    {
        PrimaryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private string T(string key, string fallback) => _translate(key, fallback);

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);
}
