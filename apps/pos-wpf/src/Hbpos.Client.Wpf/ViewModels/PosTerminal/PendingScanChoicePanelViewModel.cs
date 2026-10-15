using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>队列进度小圆点：第一个是当前正在处理的待选。</summary>
public sealed record PendingQueueDot(string Label, bool IsCurrent);

/// <summary>
/// 收银页「待选条」的界面状态：只投影 <see cref="PendingScanChoiceQueue"/> 的内容、管理收起 / 展开、
/// 放弃二次确认和「还有待选未处理」的拦截提示；加购、放弃等业务动作都通过构造时注入的回调交给收银页与工作流服务，
/// 本类不接触购物车、不直接写日志。
/// </summary>
public sealed partial class PendingScanChoicePanelViewModel : ObservableObject, IDisposable
{
    /// <summary>「放弃此项」第一次点击后保持「再点一次放弃」状态的时长，之后自动还原，防止触屏误点。</summary>
    internal static readonly TimeSpan AbandonConfirmWindow = TimeSpan.FromSeconds(3);

    private const int MaxVisibleDots = 8;

    private readonly PendingScanChoiceQueue? _queue;
    private readonly ILocalizationService? _localization;
    private readonly TimeProvider _timeProvider;
    private readonly Func<PendingScanChoice, SellableItemDto, Task> _selectAsync;
    private readonly Action<PendingScanChoice> _abandonOne;
    private readonly Action<string> _abandonAll;
    private ITimer? _abandonTimer;
    private long _armedChoiceId;
    private Func<Task>? _promptContinuation;
    private bool _disposed;

    [ObservableProperty]
    private PendingScanChoice? _current;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isCollapsed;

    [ObservableProperty]
    private bool _isAbandonArmed;

    [ObservableProperty]
    private bool _isPromptOpen;

    [ObservableProperty]
    private string _promptReason = PendingChoiceReasons.AbandonAll;

    public PendingScanChoicePanelViewModel(
        PendingScanChoiceQueue? queue,
        ILocalizationService? localization,
        TimeProvider? timeProvider,
        Func<PendingScanChoice, SellableItemDto, Task> selectAsync,
        Action<PendingScanChoice> abandonOne,
        Action<string> abandonAll)
    {
        _queue = queue;
        _localization = localization;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _selectAsync = selectAsync;
        _abandonOne = abandonOne;
        _abandonAll = abandonAll;

        // 命令名与候选卡片模板里绑定的 SelectMatchCommand 一致：手工搜索下拉和待选条共用同一套卡片模板。
        SelectMatchCommand = new AsyncRelayCommand<SellableItemDto>(SelectAsync);
        AbandonCommand = new RelayCommand(Abandon);
        AbandonAllCommand = new RelayCommand(() => ShowPrompt(PendingChoiceReasons.AbandonAll, null));
        ToggleCollapsedCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
        ConfirmPromptCommand = new AsyncRelayCommand(ConfirmPromptAsync);
        CancelPromptCommand = new RelayCommand(CancelPrompt);

        if (_queue is not null)
        {
            _queue.Changed += OnQueueChanged;
        }

        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        Refresh();
    }

    public IAsyncRelayCommand<SellableItemDto> SelectMatchCommand { get; }

    public IRelayCommand AbandonCommand { get; }

    public IRelayCommand AbandonAllCommand { get; }

    public IRelayCommand ToggleCollapsedCommand { get; }

    public IAsyncRelayCommand ConfirmPromptCommand { get; }

    public IRelayCommand CancelPromptCommand { get; }

    public bool HasPending => Count > 0;

    /// <summary>展开形态：显示队首的候选卡片。</summary>
    public bool IsExpandedVisible => HasPending && !IsCollapsed;

    /// <summary>收起形态：只剩一条细条，待选没处理完不会消失。</summary>
    public bool IsCollapsedBarVisible => HasPending && IsCollapsed;

    public bool CanAbandonAll => Count >= 2;

    public IReadOnlyList<SellableItemDto> CurrentCandidates => Current?.Candidates ?? [];

    public IReadOnlyList<PendingQueueDot> QueueDots
    {
        get
        {
            var visible = Math.Min(Count, MaxVisibleDots);
            var dots = new List<PendingQueueDot>(visible + 1);
            for (var index = 0; index < visible; index++)
            {
                dots.Add(new PendingQueueDot((index + 1).ToString(CultureInfo.InvariantCulture), index == 0));
            }

            if (Count > visible)
            {
                dots.Add(new PendingQueueDot("…", false));
            }

            return dots;
        }
    }

    public string BadgeText => Format("pendingChoice.badge", "Pending {0} / {1}", 1, Count);

    public string TitleText => Current is null
        ? string.Empty
        : Format("pendingChoice.title", "Barcode …{0} matches {1} items. Choose one.", Current.BarcodeTail, Current.Candidates.Count);

    public string ScannedAtText => Current is null
        ? string.Empty
        : Format("pendingChoice.scannedAt", "{0} scanned", Current.CreatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

    public string SameBarcodeText => T("pendingChoice.sameBarcode", "Same barcode as the previous pending item");

    public bool ShowSameBarcodeHint => Current?.SameBarcodeAsPrevious == true;

    public string HintText => T("pendingChoice.hint", "Keep scanning: other items are added as usual.");

    public string QueueLabelText => T("pendingChoice.queue", "Queue");

    public string CollapseText => T("pendingChoice.collapse", "Collapse ▴");

    public string ExpandText => T("pendingChoice.expandAction", "Tap to expand ▾");

    public string CollapsedBarText => Current is null
        ? string.Empty
        : Format(
            "pendingChoice.collapsedBar",
            "{0} pending · earliest {1} · barcode …{2}",
            Count,
            Current.CreatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Current.BarcodeTail);

    public string AbandonText => IsAbandonArmed
        ? T("pendingChoice.abandonConfirm", "Tap again to discard")
        : T("pendingChoice.abandon", "Discard this");

    public string AbandonAllText => Format("pendingChoice.abandonAll", "Discard all ({0})…", Count);

    /// <summary>右侧状态卡片里常驻的一行橙色提示；没有待选时为空。</summary>
    public string StatusHintText => HasPending
        ? Format("pendingChoice.statusHint", "{0} pending selection(s) not handled", Count)
        : string.Empty;

    public string PromptTitleText => Format("pendingChoice.prompt.title", "{0} pending item(s) not handled", Count);

    public string PromptBodyText => T(
        "pendingChoice.prompt.body." + PromptReason,
        "These items are not in the cart yet. Choose the products first, or confirm discarding them.");

    public string PromptBackText => T("pendingChoice.prompt.back", "Go back");

    public string PromptConfirmText => Format(
        "pendingChoice.prompt.confirm." + PromptReason,
        "Discard {0}",
        Count);

    /// <summary>
    /// 收银页要执行付款 / 挂单 / 取单 / 退出之前先问一次：有待选就弹出确认并返回 true（调用方应中止本次操作）。
    /// 收银员确认放弃后由 <paramref name="continuation"/> 重新发起原操作；点「返回处理」则什么都不做。
    /// </summary>
    public bool TryBlock(string reason, Func<Task> continuation)
    {
        if (Count == 0)
        {
            return false;
        }

        ShowPrompt(reason, continuation);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _abandonTimer?.Dispose();
        _abandonTimer = null;
        if (_queue is not null)
        {
            _queue.Changed -= OnQueueChanged;
        }

        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }
    }

    partial void OnIsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsExpandedVisible));
        OnPropertyChanged(nameof(IsCollapsedBarVisible));
    }

    partial void OnIsAbandonArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(AbandonText));
    }

    partial void OnPromptReasonChanged(string value)
    {
        RaisePromptTextChanged();
    }

    /// <summary>按队列当前内容重建界面投影；入队 / 出队 / 清空 / 切换语言后都会调用。</summary>
    internal void Refresh()
    {
        var snapshot = _queue?.Snapshot() ?? [];
        Count = snapshot.Count;
        Current = snapshot.Count == 0 ? null : snapshot[0];

        if (Count == 0)
        {
            // 队列清空：恢复展开默认值，并收掉可能还开着的确认框。
            IsCollapsed = false;
            IsPromptOpen = false;
            _promptContinuation = null;
        }

        if (IsAbandonArmed && Current?.Id != _armedChoiceId)
        {
            DisarmAbandon();
        }

        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(IsExpandedVisible));
        OnPropertyChanged(nameof(IsCollapsedBarVisible));
        OnPropertyChanged(nameof(CanAbandonAll));
        OnPropertyChanged(nameof(CurrentCandidates));
        OnPropertyChanged(nameof(QueueDots));
        OnPropertyChanged(nameof(ShowSameBarcodeHint));
        RaiseTextChanged();
        RaisePromptTextChanged();
    }

    private async Task SelectAsync(SellableItemDto? item)
    {
        var current = Current;
        if (item is null || current is null)
        {
            return;
        }

        DisarmAbandon();
        await _selectAsync(current, item);
    }

    private void Abandon()
    {
        var current = Current;
        if (current is null)
        {
            return;
        }

        if (!IsAbandonArmed)
        {
            // 第一次点击只「上膛」，3 秒内再点一次才真正放弃；超时自动还原。
            _armedChoiceId = current.Id;
            IsAbandonArmed = true;
            _abandonTimer?.Dispose();
            _abandonTimer = _timeProvider.CreateTimer(
                _ => IsAbandonArmed = false,
                null,
                AbandonConfirmWindow,
                Timeout.InfiniteTimeSpan);
            return;
        }

        DisarmAbandon();
        _abandonOne(current);
    }

    private void DisarmAbandon()
    {
        _abandonTimer?.Dispose();
        _abandonTimer = null;
        _armedChoiceId = 0;
        IsAbandonArmed = false;
    }

    private void ShowPrompt(string reason, Func<Task>? continuation)
    {
        if (Count == 0)
        {
            return;
        }

        _promptContinuation = continuation;
        PromptReason = reason;
        IsPromptOpen = true;
        RaisePromptTextChanged();
    }

    private void CancelPrompt()
    {
        IsPromptOpen = false;
        _promptContinuation = null;
        // 「返回处理」：把待选条展开，方便马上选。
        IsCollapsed = false;
    }

    private async Task ConfirmPromptAsync()
    {
        var reason = PromptReason;
        var continuation = _promptContinuation;
        IsPromptOpen = false;
        _promptContinuation = null;
        _abandonAll(reason);
        if (continuation is not null)
        {
            await continuation();
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    Refresh();
                }
            }));
            return;
        }

        if (!_disposed)
        {
            Refresh();
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseTextChanged();
        RaisePromptTextChanged();
    }

    private void RaiseTextChanged()
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ScannedAtText));
        OnPropertyChanged(nameof(SameBarcodeText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(QueueLabelText));
        OnPropertyChanged(nameof(CollapseText));
        OnPropertyChanged(nameof(ExpandText));
        OnPropertyChanged(nameof(CollapsedBarText));
        OnPropertyChanged(nameof(AbandonText));
        OnPropertyChanged(nameof(AbandonAllText));
        OnPropertyChanged(nameof(StatusHintText));
    }

    private void RaisePromptTextChanged()
    {
        OnPropertyChanged(nameof(PromptTitleText));
        OnPropertyChanged(nameof(PromptBodyText));
        OnPropertyChanged(nameof(PromptBackText));
        OnPropertyChanged(nameof(PromptConfirmText));
    }

    private string T(string key, string fallback)
    {
        var value = _localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        return string.Format(
            _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            T(key, fallback),
            args);
    }
}

/// <summary>待选被清掉 / 拦截的原因，同时是日志里的 reason 值和资源键后缀。</summary>
public static class PendingChoiceReasons
{
    public const string Cashier = "cashier";
    public const string Pay = "pay";
    public const string Hold = "hold";
    public const string Recall = "recall";
    public const string Exit = "exit";
    public const string AbandonAll = "all";
    public const string ClearCart = "clear-cart";
    public const string Session = "session";
    public const string Disposed = "view-model-disposed";
}
