using System.Globalization;
using System.Text.Json;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>恢复中心状态色调：只决定标签与色条颜色，不参与任何恢复判断。</summary>
public enum CardRecoveryTone
{
    Neutral,
    Info,
    Warning,
    Danger,
    Success
}

/// <summary>
/// 恢复中心的触屏展示层：状态色调、进度步骤、筛选分段、证据快捷填写与自动检查倒计时。
/// 只读取已加载的队列快照并复用现有命令，金融恢复与主管结案逻辑仍在主文件中。
/// </summary>
public sealed partial class CardRecoveryCenterViewModel
{
    internal const int AutoCheckIntervalSeconds = 30;

    private int _autoCheckSecondsRemaining = AutoCheckIntervalSeconds;
    private bool _isAutoChecking;
    private IRelayCommand<string>? _applyEvidencePresetCommand;
    private IRelayCommand? _openManualCommand;
    private IRelayCommand? _closeManualCommand;

    // ---------- 页签与筛选 ----------

    public int PendingCount => _history.Count(x => x.IsOpen);
    public int FailedCount => _history.Count(IsFailed);
    public int ResolvedCount => _history.Count(x => !x.IsOpen && !IsFailed(x));
    public int ReviewCount => _history.Count(IsReview);

    public bool IsChannelAll { get => ChannelIndex == 0; set { if (value) ChannelIndex = 0; } }
    public bool IsChannelLinkly { get => ChannelIndex == 1; set { if (value) ChannelIndex = 1; } }
    public bool IsChannelSquare { get => ChannelIndex == 2; set { if (value) ChannelIndex = 2; } }
    public bool IsOperationAll { get => OperationIndex == 0; set { if (value) OperationIndex = 0; } }
    public bool IsOperationSale { get => OperationIndex == 1; set { if (value) OperationIndex = 1; } }
    public bool IsOperationRefund { get => OperationIndex == 2; set { if (value) OperationIndex = 2; } }

    public string ListCountText => string.Format(
        GetCulture(),
        T("cardRecovery.v2.listCount", "{0} transactions"),
        OpenAttemptRows.Count);

    // ---------- 详情头部 ----------

    public bool IsSelectedRefund => IsRefundSelection;
    public CardRecoveryTone SelectedTone => SelectedAttempt is null ? CardRecoveryTone.Neutral : ToneFor(SelectedAttempt);
    public string SelectedStatusPillText => SelectedAttempt is null ? NoneText : StatusPillFor(SelectedAttempt);
    public string SelectedTitleText => SelectedAttempt is null
        ? NoneText
        : $"{SelectedTypeText} · {SelectedChannelText}";
    public string SelectedHeadlineText => IsRefundSelection
        ? T("cardRecovery.v2.headline.refund", "Refund to original card")
        : IsPaymentSelection
            ? T("cardRecovery.v2.headline.sale", "Card payment")
            : SelectedTypeText;
    public string SelectedCashierNameText => ValueOrNone(
        ReadDraftString(SelectedAttempt?.OrderDraftJson, "session", "cashierName") ?? SelectedAttempt?.CashierId);
    public string SelectedProductSummaryText => HasProductSnapshot
        ? string.Format(
            GetCulture(),
            T("cardRecovery.v2.productsCount", "{0} items"),
            SelectedProductLines.Sum(line => line.Quantity))
        : string.Empty;

    // 退货单已完成但 Square 仍在结算 / 事后拒绝的退款：详情页给出各自的说明，不再显示“请勿重复退款”。
    public bool IsSettlementPendingSelection => IsSettlementPending(SelectedAttempt);
    public bool IsSettlementRejectedSelection => IsSettlementRejected(SelectedAttempt);
    public bool IsSettlementSelection => IsSettlementPendingSelection || IsSettlementRejectedSelection;
    public bool ShowAlternativeOutcomes => !IsSettlementRejectedSelection;
    // 被 Square 拒绝的退款只需要记录“已用其他方式退款”和备注，不需要银行证据与 Square 参考号。
    public bool ShowEvidenceInputs => !IsSettlementRejectedSelection;
    public bool ShowActionBar => SelectedAttempt is { } attempt && CanRecover(attempt);

    public bool ShowSafetyBanner => SelectedAttempt is { IsOpen: true } || IsSettlementPendingSelection;
    public string SafetyBannerTitleText => IsSettlementRejectedSelection
        ? T("cardRecovery.settlement.banner.rejected.title", "Square rejected this refund.")
        : IsSettlementPendingSelection
            ? T("cardRecovery.settlement.banner.pending.title", "The return is complete. Square is settling the refund.")
            : IsRefundSelection
        ? T("cardRecovery.v2.banner.refund.title", "Do not refund again.")
        : T("cardRecovery.v2.banner.payment.title", "Do not charge twice.");
    public string SafetyBannerBodyText => IsSettlementRejectedSelection
        ? T("cardRecovery.settlement.banner.rejected.body", "The customer has not received this money. Refund them by cash or another method, then tap \"Refunded by other means\".")
        : IsSettlementPendingSelection
            ? T("cardRecovery.settlement.banner.pending.body", "Square accepted the refund and is sending it to the original card. Most refunds complete within a few hours (up to 14 days). No card tap is needed and no action is required.")
            : IsSquareRefundProcessing
        ? T(
            "cardRecovery.v2.banner.squareRefund.body",
            "The refund was sent to Square and is still being processed. No card tap is needed. The order completes once Square confirms.")
        : IsRefundSelection
            ? T(
                "cardRecovery.v2.banner.refund.body",
                "Check the original refund result before refunding again by card or cash.")
            : T("cardRecovery.workspace.warning", "Check the original result before taking any further payment. Do not charge twice.");

    // ---------- 进度步骤：已提交 → 当前状态 → 完成/结束 ----------

    public string StepSubmittedTimeText => SelectedAttempt is null
        ? NoneText
        : SelectedAttempt.CreatedAt.ToLocalTime().ToString("T", GetCulture());
    public string StepCurrentTimeText => SelectedAttempt is null
        ? NoneText
        : string.Format(
            GetCulture(),
            T("cardRecovery.v2.step.since", "since {0}"),
            SelectedAttempt.UpdatedAt.ToLocalTime().ToString("T", GetCulture()));
    public bool IsFinalStepDone => SelectedAttempt is { IsOpen: false } attempt && !IsFailed(attempt) && !IsSettlementPending(attempt);
    public bool IsFinalStepFailed => IsSettlementRejectedSelection || (SelectedAttempt is { IsOpen: false } attempt && IsFailed(attempt));
    public bool IsFinalStepPending => (SelectedAttempt is { IsOpen: true } && !IsSettlementRejectedSelection) || IsSettlementPendingSelection;
    // 第 2 步：普通交易为“处理中”（琥珀色圆环）；退货单已完成的退款第 2 步已经完成（绿色对勾）。
    public bool IsCurrentStepActive => IsFinalStepPending && !IsSettlementSelection;
    public string StepCurrentLabelText => IsSettlementSelection
        ? T("cardRecovery.settlement.step.returnCompleted", "Return completed")
        : SelectedStatusPillText;
    public string StepFinalLabelText => IsSettlementPendingSelection
        ? T("cardRecovery.settlement.step.settling", "Square settling to card")
        : IsFinalStepFailed
        ? SelectedStatusText
        : IsRefundSelection
            ? T("cardRecovery.v2.step.refundCompleted", "Refund completed")
            : T("cardRecovery.v2.step.paymentCompleted", "Payment completed");
    public string StepFinalTimeText => IsFinalStepFailed
        ? SelectedAttempt?.UpdatedAt.ToLocalTime().ToString("T", GetCulture()) ?? NoneText
        : IsFinalStepPending
        ? T("cardRecovery.v2.step.waiting", "Waiting")
        : SelectedAttempt?.UpdatedAt.ToLocalTime().ToString("T", GetCulture()) ?? NoneText;

    // ---------- 人工核对面板 ----------

    public IRelayCommand OpenManualCommand => _openManualCommand ??= new RelayCommand(
        () => IsManualExpanded = true,
        () => CanShowSupervisorResolution);

    public IRelayCommand CloseManualCommand => _closeManualCommand ??= new RelayCommand(() => IsManualExpanded = false);

    /// <summary>证据快捷标签：把本地化的证据来源写入证据输入框，主管仍可继续编辑。</summary>
    public IRelayCommand<string> ApplyEvidencePresetCommand => _applyEvidencePresetCommand ??= new RelayCommand<string>(key =>
    {
        var text = key switch
        {
            "squareDashboard" => T("cardRecovery.v2.evidence.squareDashboard", "Square Dashboard"),
            "terminalReceipt" => T("cardRecovery.v2.evidence.terminalReceipt", "Terminal receipt"),
            "bankStatement" => T("cardRecovery.v2.evidence.bankStatement", "Bank statement"),
            _ => null
        };
        if (text is null)
        {
            return;
        }

        var current = Normalize(ResolutionEvidence);
        ResolutionEvidence = current is null || current.Contains(text, StringComparison.OrdinalIgnoreCase)
            ? current ?? text
            : $"{current}; {text}";
    });

    public string ProcessedOutcomeHintText => IsSettlementRejectedSelection
        ? T("cardRecovery.settlement.outcome.refundedOther", "The customer was paid back by cash or another method.")
        : IsRefundSelection
        ? T("cardRecovery.v2.outcome.processed.refund", "The refund reached the customer's card.")
        : T("cardRecovery.v2.outcome.processed.sale", "The customer's card was charged.");
    public string NotProcessedOutcomeHintText => IsRefundSelection
        ? T("cardRecovery.v2.outcome.notProcessed.refund", "No refund was made. The return stays open for a new refund.")
        : T("cardRecovery.v2.outcome.notProcessed.sale", "No charge was made. The order can be paid again.");
    public string WaitingOutcomeHintText => T(
        "cardRecovery.v2.outcome.waiting",
        "Keep the transaction locked and check again later. A supervisor note is required.");

    // ---------- 自动检查 ----------

    /// <summary>
    /// 只对“Square 已受理、仍在处理中”的退款自动查询：查询只读取同一笔退款，不会重复退款；
    /// 且当前收银员自身具备查看权限，避免每 30 秒弹出主管扫码。
    /// </summary>
    public bool IsAutoCheckActive =>
        SelectedAttempt is { IsOpen: true } &&
        IsSquareRefundProcessing &&
        !IsManualExpanded &&
        CurrentCashierCanViewPayments();

    public string AutoCheckText => _isAutoChecking || (IsBusy && IsAutoCheckActive)
        ? T("cardRecovery.v2.auto.checking", "Checking now…")
        : IsAutoCheckActive
            ? string.Format(
                GetCulture(),
                T("cardRecovery.v2.auto.countdown", "Checking automatically · next check in {0}"),
                TimeSpan.FromSeconds(_autoCheckSecondsRemaining).ToString(@"m\:ss", CultureInfo.InvariantCulture))
            : T("cardRecovery.v2.auto.manualOnly", "Tap Check status now to query the latest result.");

    internal int AutoCheckSecondsRemaining => _autoCheckSecondsRemaining;

    /// <summary>由界面每秒调用一次；倒计时归零时执行与“立即检查”相同的定点恢复命令。</summary>
    internal async Task OnAutoCheckTickAsync()
    {
        if (!IsAutoCheckActive || IsBusy || _isAutoChecking)
        {
            ResetAutoCheckCountdown();
            return;
        }

        _autoCheckSecondsRemaining--;
        if (_autoCheckSecondsRemaining > 0)
        {
            OnPropertyChanged(nameof(AutoCheckText));
            return;
        }

        _isAutoChecking = true;
        OnPropertyChanged(nameof(AutoCheckText));
        try
        {
            if (RecoverCommand.CanExecute(null))
            {
                await RecoverCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            _isAutoChecking = false;
            ResetAutoCheckCountdown();
        }
    }

    private void ResetAutoCheckCountdown()
    {
        _autoCheckSecondsRemaining = AutoCheckIntervalSeconds;
        OnPropertyChanged(nameof(AutoCheckText));
        OnPropertyChanged(nameof(IsAutoCheckActive));
    }

    private bool CurrentCashierCanViewPayments() =>
        _session.CashierSession is { } cashier &&
        (cashier.IsSuperAdmin ||
         cashier.PermissionCodes.Contains(Permissions.PosTerminal.Payment.View, StringComparer.Ordinal));

    // ---------- 行与状态 ----------

    private CardRecoveryTone ToneFor(CardRecoveryQueueItem item)
    {
        if (IsSettlementPending(item))
        {
            return CardRecoveryTone.Info;
        }

        if (!item.IsOpen)
        {
            return IsFailed(item) ? CardRecoveryTone.Neutral : CardRecoveryTone.Success;
        }

        if (IsReview(item))
        {
            return CardRecoveryTone.Danger;
        }

        return HasSquareRefundPaymentEvidence(item) || IsRefundKind(item)
            ? CardRecoveryTone.Warning
            : CardRecoveryTone.Info;
    }

    private string StatusPillFor(CardRecoveryQueueItem item) =>
        item.IsOpen && HasSquareRefundPaymentEvidence(item)
            ? T("cardRecovery.v2.pill.squareRefundProcessing", "Processing at Square")
            : MapStatus(item.Status);

    private string ListTimeText(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var time = local.ToString("t", GetCulture());
        return local.Date == DateTime.Today
            ? string.Format(GetCulture(), T("cardRecovery.v2.today", "Today {0}"), time)
            : $"{local.ToString("d MMM", GetCulture())} {time}";
    }

    private static bool IsRefundKind(CardRecoveryQueueItem item) =>
        string.Equals(item.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase);

    private static string ShortOrder(string order) =>
        order.Length <= 14 ? order : "…" + order[^12..];

    private static string? ReadDraftString(string? json, string objectName, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var container in document.RootElement.EnumerateObject())
            {
                if (!container.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase) ||
                    container.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in container.Value.EnumerateObject())
                {
                    if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        return Normalize(property.Value.GetString());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 草稿损坏时只回退显示收银员编号，不影响恢复。
        }

        return null;
    }

    private void NotifyPresentationProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(PendingCount), nameof(FailedCount), nameof(ResolvedCount), nameof(ReviewCount),
                     nameof(IsChannelAll), nameof(IsChannelLinkly), nameof(IsChannelSquare),
                     nameof(IsOperationAll), nameof(IsOperationSale), nameof(IsOperationRefund),
                     nameof(ListCountText), nameof(IsSelectedRefund), nameof(SelectedTone), nameof(SelectedStatusPillText),
                     nameof(SelectedTitleText), nameof(SelectedHeadlineText), nameof(SelectedCashierNameText),
                     nameof(SelectedProductSummaryText), nameof(ShowSafetyBanner), nameof(SafetyBannerTitleText),
                     nameof(SafetyBannerBodyText), nameof(StepSubmittedTimeText), nameof(StepCurrentTimeText),
                     nameof(IsFinalStepDone), nameof(IsFinalStepFailed), nameof(IsFinalStepPending),
                     nameof(IsSettlementPendingSelection), nameof(IsSettlementRejectedSelection), nameof(IsSettlementSelection),
                     nameof(ShowAlternativeOutcomes), nameof(ShowEvidenceInputs), nameof(ShowActionBar), nameof(IsCurrentStepActive), nameof(StepCurrentLabelText),
                     nameof(StepFinalLabelText), nameof(StepFinalTimeText), nameof(ProcessedOutcomeHintText),
                     nameof(NotProcessedOutcomeHintText), nameof(WaitingOutcomeHintText)
                 })
        {
            OnPropertyChanged(name);
        }

        OpenManualCommand.NotifyCanExecuteChanged();
        ResetAutoCheckCountdown();
    }
}
