using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed record CardRecoveryHistoryEvent(string TimeText, string Description);

public sealed partial class CardRecoveryCenterViewModel
{
    private IReadOnlyList<CardRecoveryQueueItem> _history = [];
    private DateTimeOffset? _lastRefresh;
    private string _searchText = string.Empty;
    private string _category = "pending";
    private int _channelIndex;
    private int _operationIndex;
    private bool _isManualExpanded;
    private IRelayCommand<string>? _filterCommand;
    private IRelayCommand? _toggleManualCommand;

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ApplyFilters(); } }
    public int ChannelIndex { get => _channelIndex; set { if (SetProperty(ref _channelIndex, value)) ApplyFilters(); } }
    public int OperationIndex { get => _operationIndex; set { if (SetProperty(ref _operationIndex, value)) ApplyFilters(); } }
    public bool IsManualExpanded
    {
        get => _isManualExpanded;
        set
        {
            if (SetProperty(ref _isManualExpanded, value))
            {
                // 人工核对面板打开期间暂停自动检查，避免查询结果把主管正在核对的交易刷新掉。
                ResetAutoCheckCountdown();
            }
        }
    }
    public IRelayCommand<string> FilterCommand => _filterCommand ??= new RelayCommand<string>(value => { _category = value ?? "pending"; ApplyFilters(); });
    public IRelayCommand ToggleManualCommand => _toggleManualCommand ??= new RelayCommand(() => IsManualExpanded = !IsManualExpanded);
    public bool IsPendingFilter => _category == "pending";
    public bool IsFailedFilter => _category == "failed";
    public bool IsResolvedFilter => _category == "resolved";
    public bool IsReviewFilter => _category == "review";
    public string PendingCountText => LabelCount("pending", _history.Count(x => x.IsOpen));
    public string FailedCountText => LabelCount("failed", _history.Count(IsFailed));
    public string ResolvedCountText => LabelCount("resolved", _history.Count(x => !x.IsOpen && !IsFailed(x)));
    public string ReviewCountText => LabelCount("review", _history.Count(IsReview));
    public string LastRefreshText => _lastRefresh?.ToString("G", GetCulture()) ?? NoneText;
    public string ConfirmProcessedText => IsRefundSelection
        ? T("cardRecovery.refund.action.confirmRefunded", "Confirmed refunded")
        : T("cardRecovery.center.action.confirmPaid", "Confirmed paid");
    public string ConfirmNotProcessedText => IsRefundSelection
        ? T("cardRecovery.refund.action.confirmNotRefunded", "Confirmed not refunded")
        : T("cardRecovery.center.action.confirmNotPaid", "Confirmed not paid");
    // 切换界面语言只改变数字格式，不能把澳元交易显示成人民币。
    private string FormatAmount(decimal amount) => $"AU${amount.ToString("N2", GetCulture())}";
    public string SelectedOrderText => OrderReference(SelectedAttempt);
    public bool IsHistorySelection => SelectedAttempt is { IsOpen: false };
    public IReadOnlyList<CardRecoveryHistoryEvent> SelectedHistory => SelectedAttempt is { } item
        ? [new(item.CreatedAt.ToLocalTime().ToString("G", GetCulture()), T("cardRecovery.workspace.created", "Transaction recorded")),
           new(item.UpdatedAt.ToLocalTime().ToString("G", GetCulture()), MapStatus(item.Status))] : [];

    private string LabelCount(string key, int count) => $"{T("cardRecovery.workspace." + key, key)}  {count}";
    private static bool IsFailed(CardRecoveryQueueItem item) => !item.IsOpen &&
        item.Status is "Declined" or "Cancelled" or "Canceled" or "Failed" or "Abandoned" or "TimedOut";
    private static bool IsReview(CardRecoveryQueueItem item) => item.IsOpen && item.Status is "RequiresReview" or "Unknown";
    private IEnumerable<CardRecoveryQueueItem> FilteredHistory() => _history.Where(item =>
        (_category switch { "failed" => IsFailed(item), "resolved" => !item.IsOpen && !IsFailed(item), "review" => IsReview(item), _ => item.IsOpen }) &&
        (ChannelIndex == 0 || item.Processor == (ChannelIndex == 1 ? CardProcessorKind.Linkly : CardProcessorKind.Square)) &&
        (OperationIndex == 0 || (OperationIndex == 2 ? item.OperationKind == "Refund" : item.OperationKind != "Refund")) &&
        (string.IsNullOrWhiteSpace(SearchText) || string.Join(" ", OrderReference(item), item.AttemptGuid, item.TxnRef, item.CheckoutId, item.SessionId, item.PaymentId, item.PaymentReference)
            .Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)));

    private void ApplyFilters()
    {
        RefreshDisplayRows();
        if (SelectedAttempt is null) SelectedAttempt = OpenAttemptRows.FirstOrDefault()?.Source;
        NotifyWorkspaceProperties();
        OnPropertyChanged(nameof(HasNoOpenAttempts));
    }
    private void NotifyWorkspaceProperties()
    {
        foreach (var name in new[] { nameof(PendingCountText), nameof(FailedCountText), nameof(ResolvedCountText), nameof(ReviewCountText),
            nameof(IsPendingFilter), nameof(IsFailedFilter), nameof(IsResolvedFilter), nameof(IsReviewFilter), nameof(LastRefreshText),
            nameof(ConfirmProcessedText), nameof(ConfirmNotProcessedText), nameof(SelectedOrderText), nameof(SelectedHistory), nameof(IsHistorySelection) }) OnPropertyChanged(name);
        NotifyPresentationProperties();
    }
    internal static string OrderReference(CardRecoveryQueueItem? item)
    {
        if (item is null) return "—";
        if (!string.IsNullOrWhiteSpace(item.OrderDraftJson))
        {
            try
            {
                using var json = JsonDocument.Parse(item.OrderDraftJson);
                foreach (var property in json.RootElement.EnumerateObject())
                    if (property.Name.Equals("orderGuid", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString() ?? item.AttemptGuid.ToString("D");
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return item.AttemptGuid.ToString("D");
    }
}
