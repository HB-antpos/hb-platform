using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using BlazorApp.Shared.Constants;

namespace Hbpos.Client.Wpf.ViewModels;

/// <summary>
/// 收银主界面「钱箱」：小面板（今日存入 / 取出合计、存入、取出、只开钱箱、今日记录）、
/// 存入 / 取出弹窗（原因 → 金额 → 备注 → 确认）、今日记录列表（补打、冲正）。
/// 确认顺序：权限校验 → 写本地 → 弹钱箱 → 打 1 张凭条 → 记审计；后台上传由日结上传 Worker 负责。
/// </summary>
public sealed partial class CashMovementViewModel : ObservableObject
{
    private const int MaxAmountDigits = 9;

    private readonly ICashMovementService _service;
    private readonly ICashMovementPrintService? _printService;
    private readonly Func<PosSessionState> _getSession;
    private readonly Func<string, string, Task<ViewModelAuthorizationGrant?>> _authorizeAsync;
    private readonly Func<Task<ReceiptPrintResult>>? _openCashDrawerAsync;
    private readonly Func<CancellationToken, Task<decimal?>>? _loadExpectedCashAsync;
    private readonly IOperationAuditLogger? _auditLogger;
    private readonly Func<string, string> _translate;
    private readonly Action<string, bool> _setStatus;
    private CashMovementPeriodSnapshot? _snapshot;
    private CashMovementRowViewModel? _voidTarget;

    internal CashMovementViewModel(
        ICashMovementService service,
        ICashMovementPrintService? printService,
        Func<PosSessionState> getSession,
        Func<string, string, Task<ViewModelAuthorizationGrant?>> authorizeAsync,
        Func<Task<ReceiptPrintResult>>? openCashDrawerAsync,
        Func<CancellationToken, Task<decimal?>>? loadExpectedCashAsync,
        IOperationAuditLogger? auditLogger,
        Func<string, string> translate,
        Action<string, bool> setStatus)
    {
        _service = service;
        _printService = printService;
        _getSession = getSession;
        _authorizeAsync = authorizeAsync;
        _openCashDrawerAsync = openCashDrawerAsync;
        _loadExpectedCashAsync = loadExpectedCashAsync;
        _auditLogger = auditLogger;
        _translate = translate;
        _setStatus = setStatus;
        foreach (var amount in new[] { 20m, 50m, 100m, 200m, 500m })
        {
            QuickAmounts.Add(amount.ToString("0", CultureInfo.InvariantCulture));
        }
    }

    public ObservableCollection<CashMovementReasonOption> ReasonOptions { get; } = [];

    public ObservableCollection<string> QuickAmounts { get; } = [];

    public ObservableCollection<string> QuickNotes { get; } = [];

    public ObservableCollection<CashMovementRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _isPanelOpen;

    [ObservableProperty]
    private bool _isDialogOpen;

    [ObservableProperty]
    private bool _isRecordsOpen;

    [ObservableProperty]
    private bool _isVoidConfirmOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCashIn), nameof(DialogTitle), nameof(ConfirmText))]
    private bool _isCashOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText), nameof(ExpectedAfterText))]
    private string _amountText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotePlaceholder))]
    private string? _selectedReason;

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private string _voidNoteText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _todayCashInText = Money(0m);

    [ObservableProperty]
    private string _todayCashOutText = Money(0m);

    [ObservableProperty]
    private string _todayCountText = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpectedAfterText), nameof(HasExpectedCash))]
    private decimal? _expectedCash;

    [ObservableProperty]
    private string _voidTargetText = string.Empty;

    public bool IsCashIn => !IsCashOut;

    public string DialogTitle => T(IsCashOut ? "cashMovement.dialog.cashOutTitle" : "cashMovement.dialog.cashInTitle");

    public string ConfirmText => string.Format(
        CultureInfo.CurrentCulture,
        T(IsCashOut ? "cashMovement.dialog.confirmOut" : "cashMovement.dialog.confirmIn"),
        Money(ParseAmount() ?? 0m));

    public string NotePlaceholder => SelectedReason is { } reason && CashMovementReasons.IsNoteRequired(reason)
        ? T("cashMovement.dialog.noteRequired")
        : T("cashMovement.dialog.noteOptional");

    public bool HasExpectedCash => ExpectedCash is not null;

    public string ExpectedBeforeText => ExpectedCash is { } value ? Money(value) : string.Empty;

    public string ExpectedAfterText
    {
        get
        {
            if (ExpectedCash is not { } before)
            {
                return string.Empty;
            }

            var amount = ParseAmount() ?? 0m;
            return Money(IsCashOut ? before - amount : before + amount);
        }
    }

    public string TodayTitle => string.Format(
        CultureInfo.CurrentCulture,
        T("cashMovement.records.title"),
        _service.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    // ───────────────────────── 面板 ─────────────────────────

    [RelayCommand]
    private async Task OpenPanelAsync()
    {
        ErrorText = null;
        IsPanelOpen = true;
        await RefreshAsync();
    }

    [RelayCommand]
    private void ClosePanel() => IsPanelOpen = false;

    [RelayCommand]
    private async Task NoSaleAsync(IAsyncRelayCommand? noSaleCommand)
    {
        // 「只开钱箱」= 原来的 No Sale：权限、再扫码设置与 CASH_DRAWER_OPEN 审计全部沿用原命令。
        IsPanelOpen = false;
        if (noSaleCommand is not null && noSaleCommand.CanExecute(null))
        {
            await noSaleCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private Task StartCashInAsync() => StartDialogAsync(cashOut: false);

    [RelayCommand]
    private Task StartCashOutAsync() => StartDialogAsync(cashOut: true);

    [RelayCommand]
    private async Task OpenRecordsAsync()
    {
        IsPanelOpen = false;
        IsRecordsOpen = true;
        OnPropertyChanged(nameof(TodayTitle));
        await RefreshAsync();
    }

    [RelayCommand]
    private void CloseRecords() => IsRecordsOpen = false;

    // ───────────────────────── 存入 / 取出弹窗 ─────────────────────────

    [RelayCommand]
    private void SelectDirection(string? direction)
    {
        var cashOut = string.Equals(direction, "Out", StringComparison.OrdinalIgnoreCase);
        if (cashOut != IsCashOut)
        {
            IsCashOut = cashOut;
            LoadReasonOptions();
        }
    }

    [RelayCommand]
    private void SelectReason(string? reason)
    {
        if (reason is null || !CashMovementReasons.IsKnown(reason))
        {
            return;
        }

        SelectedReason = reason;
        foreach (var option in ReasonOptions)
        {
            option.IsSelected = option.Code == reason;
        }

        LoadQuickNotes();
        ErrorText = null;
    }

    [RelayCommand]
    private void KeypadInput(string? key)
    {
        ErrorText = null;
        var text = AmountText;
        switch (key)
        {
            case null:
                return;
            case "Clear":
                AmountText = string.Empty;
                return;
            case "Back":
                AmountText = text.Length == 0 ? text : text[..^1];
                return;
            case ".":
                if (!text.Contains('.', StringComparison.Ordinal))
                {
                    AmountText = text.Length == 0 ? "0." : text + ".";
                }

                return;
        }

        if (key.Length != 1 || !char.IsAsciiDigit(key[0]))
        {
            return;
        }

        // 最多两位小数、最多 9 位数字，防止误按出离谱金额。
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if ((dot >= 0 && text.Length - dot > 2) || text.Count(char.IsAsciiDigit) >= MaxAmountDigits)
        {
            return;
        }

        AmountText = text == "0" ? key : text + key;
    }

    [RelayCommand]
    private void QuickAmount(string? amount)
    {
        if (decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            AmountText = value.ToString("0.##", CultureInfo.InvariantCulture);
            ErrorText = null;
        }
    }

    [RelayCommand]
    private void QuickNote(string? note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            NoteText = note;
        }
    }

    [RelayCommand]
    private void CancelDialog()
    {
        IsDialogOpen = false;
        ErrorText = null;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedReason is not { } reason)
        {
            ErrorText = T("cashMovement.error.reasonRequired");
            return;
        }

        if (ParseAmount() is not { } amount || amount <= 0m)
        {
            ErrorText = T("cashMovement.error.invalidAmount");
            return;
        }

        var permission = IsCashOut
            ? Permissions.PosTerminal.CashMovement.CashOut
            : Permissions.PosTerminal.CashMovement.CashIn;
        IsBusy = true;
        try
        {
            using var grant = await _authorizeAsync(permission, IsCashOut ? "cash-out" : "cash-in");
            if (grant is null)
            {
                return;
            }

            using var activation = grant.Activate();
            var session = _getSession();
            var movement = await _service.CreateAsync(
                session,
                new CashMovementDraft(reason, amount, NoteText),
                CurrentAuthorizer(session));
            IsDialogOpen = false;
            await AfterSavedAsync(session, movement, null);
        }
        catch (CashMovementValidationException ex)
        {
            ErrorText = T($"cashMovement.error.{ex.Code}");
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("CashMovement", $"save failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            ErrorText = T("cashMovement.error.saveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ───────────────────────── 今日记录：补打 / 冲正 ─────────────────────────

    [RelayCommand]
    private async Task ReprintAsync(CashMovementRowViewModel? row)
    {
        if (row is null || _printService is null || _snapshot is null)
        {
            return;
        }

        var original = row.Movement.VoidsMovementGuid is { } voids
            ? _snapshot.Movements.FirstOrDefault(item => item.MovementGuid == voids)
            : null;
        var result = await _printService.PrintAsync(row.Movement, original, _snapshot.Totals, ReceiptPrintReason.Reprint);
        _setStatus(result.Succeeded ? T("cashMovement.status.reprinted") : result.Message, result.Succeeded);
    }

    [RelayCommand]
    private void StartVoid(CashMovementRowViewModel? row)
    {
        if (row is null || !row.CanVoid)
        {
            return;
        }

        _voidTarget = row;
        VoidTargetText = $"{row.DisplayNumber}  {row.ReasonText}  {row.AmountText}";
        VoidNoteText = string.Empty;
        ErrorText = null;
        IsVoidConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelVoid()
    {
        _voidTarget = null;
        IsVoidConfirmOpen = false;
        ErrorText = null;
    }

    [RelayCommand]
    private async Task ConfirmVoidAsync()
    {
        if (IsBusy || _voidTarget is not { } target)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(VoidNoteText) || VoidNoteText.Trim().Length < 2)
        {
            ErrorText = T("cashMovement.error.voidNoteRequired");
            return;
        }

        IsBusy = true;
        try
        {
            using var grant = await _authorizeAsync(Permissions.PosTerminal.CashMovement.Void, "cash-movement-void");
            if (grant is null)
            {
                return;
            }

            using var activation = grant.Activate();
            var session = _getSession();
            var reversal = await _service.VoidAsync(session, target.Movement, VoidNoteText, CurrentAuthorizer(session));
            IsVoidConfirmOpen = false;
            _voidTarget = null;
            await AfterSavedAsync(session, reversal, target.Movement);
        }
        catch (CashMovementValidationException ex)
        {
            ErrorText = T($"cashMovement.error.{ex.Code}");
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("CashMovement", $"void failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            ErrorText = T("cashMovement.error.saveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ───────────────────────── 内部 ─────────────────────────

    private async Task StartDialogAsync(bool cashOut)
    {
        IsPanelOpen = false;
        IsCashOut = cashOut;
        AmountText = string.Empty;
        NoteText = string.Empty;
        ErrorText = null;
        LoadReasonOptions();
        IsDialogOpen = true;
        ExpectedCash = null;
        if (_loadExpectedCashAsync is not null)
        {
            try
            {
                ExpectedCash = await _loadExpectedCashAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // 应有现金只是参考提示，取不到不影响存入 / 取出。
                ConsoleLog.WriteWarning("CashMovement", $"load expected cash failed error={ex.GetType().Name} message={ex.Message}");
            }
        }

        OnPropertyChanged(nameof(ExpectedBeforeText));
    }

    private async Task AfterSavedAsync(PosSessionState session, LocalCashMovement movement, LocalCashMovement? voidedOriginal)
    {
        // 记录已落库：开钱箱、打印、审计任何一步失败都只提示，不回滚记录。
        var drawerOk = true;
        if (_openCashDrawerAsync is not null)
        {
            var drawer = await _openCashDrawerAsync();
            drawerOk = drawer.Succeeded;
        }

        await RefreshAsync();
        var printOk = true;
        string? printMessage = null;
        if (_printService is not null && _snapshot is not null)
        {
            var printed = await _printService.PrintAsync(movement, voidedOriginal, _snapshot.Totals);
            printOk = printed.Succeeded;
            printMessage = printed.Message;
        }

        OperationAuditEvents.RecordAction(
            _auditLogger,
            voidedOriginal is not null
                ? OperationAuditTypes.CashMovementVoid
                : movement.Direction == CashMovementDirection.In ? OperationAuditTypes.CashIn : OperationAuditTypes.CashOut,
            "Succeeded",
            session,
            reasonCode: movement.ReasonCode,
            paymentMethod: "Cash",
            paymentAmount: movement.SignedAmount,
            orderGuid: movement.MovementGuid.ToString("D"),
            receiptNumber: movement.DisplayNumber);

        var key = voidedOriginal is not null
            ? "cashMovement.status.voided"
            : movement.Direction == CashMovementDirection.In ? "cashMovement.status.savedIn" : "cashMovement.status.savedOut";
        var message = string.Format(CultureInfo.CurrentCulture, T(key), Money(movement.Amount), movement.DisplayNumber);
        if (!drawerOk)
        {
            message += " " + T("cashMovement.status.drawerFailed");
        }

        if (!printOk)
        {
            message += " " + (printMessage ?? T("cashMovement.status.printFailed"));
        }

        _setStatus(message, drawerOk && printOk);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var session = _getSession();
            _snapshot = await _service.GetPeriodAsync(session, _service.Today);
            TodayCashInText = Money(_snapshot.Totals.CashIn);
            TodayCashOutText = Money(_snapshot.Totals.CashOut);
            TodayCountText = _snapshot.Movements.Count.ToString(CultureInfo.InvariantCulture);
            Rows.Clear();
            // 最新的在上面。
            foreach (var movement in _snapshot.Movements.Reverse())
            {
                Rows.Add(new CashMovementRowViewModel(movement, _snapshot.IsVoided(movement), _translate));
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError("CashMovement", $"load today failed error={ex.GetType().Name} message={ex.Message}", exception: ex);
            ErrorText = T("cashMovement.error.loadFailed");
        }
    }

    private void LoadReasonOptions()
    {
        ReasonOptions.Clear();
        foreach (var reason in IsCashOut ? CashMovementReasons.OutReasons : CashMovementReasons.InReasons)
        {
            ReasonOptions.Add(new CashMovementReasonOption(
                reason,
                T($"cashMovement.reason.{reason}"),
                T($"cashMovement.reason.{reason}.hint")));
        }

        // 默认选中第一项（取出默认「交店长」、存入默认「补零钱」），减少一次点击。
        SelectReason(ReasonOptions[0].Code);
    }

    private void LoadQuickNotes()
    {
        QuickNotes.Clear();
        var key = SelectedReason switch
        {
            CashMovementReasons.PaidOut => "cashMovement.quickNotes.paidOut",
            CashMovementReasons.SafeDrop => "cashMovement.quickNotes.safeDrop",
            CashMovementReasons.ChangeTopUp => "cashMovement.quickNotes.changeTopUp",
            _ => null
        };
        if (key is null)
        {
            return;
        }

        foreach (var note in T(key).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            QuickNotes.Add(note);
        }
    }

    /// <summary>授权由他人扫码完成时记录授权人；本人有权限时为 null。</summary>
    private static CashMovementAuthorizer? CurrentAuthorizer(PosSessionState session)
    {
        var authorizer = OperationAuthorizationScope.CurrentAuthorizingSession;
        if (authorizer is null || string.Equals(authorizer.CashierId, session.CashierId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new CashMovementAuthorizer(authorizer.CashierId, authorizer.CashierName);
    }

    private decimal? ParseAmount() =>
        decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? decimal.Round(value, 2, MidpointRounding.AwayFromZero)
            : null;

    private string T(string key) => _translate(key);

    internal static string Money(decimal amount) => DailyCloseMoneyFormat.Money(amount);
}

public sealed partial class CashMovementReasonOption(string code, string title, string hint) : ObservableObject
{
    public string Code { get; } = code;

    public string Title { get; } = title;

    public string Hint { get; } = hint;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class CashMovementRowViewModel(LocalCashMovement movement, bool isVoided, Func<string, string> translate)
{
    public LocalCashMovement Movement { get; } = movement;

    public string DisplayNumber => Movement.DisplayNumber;

    public string TimeText => Movement.OccurredAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public bool IsIn => Movement.Direction == CashMovementDirection.In;

    public bool IsVoidRecord => Movement.IsVoid;

    public bool IsVoided { get; } = isVoided;

    public string DirectionText => translate(Movement.IsVoid
        ? "cashMovement.records.void"
        : IsIn ? "cashMovement.records.in" : "cashMovement.records.out");

    public string ReasonText => translate($"cashMovement.reason.{Movement.ReasonCode}");

    public string AmountText => DailyCloseMoneyFormat.SignedMoney(Movement.SignedAmount);

    public string CashierText => string.IsNullOrWhiteSpace(Movement.AuthorizedByName)
        ? Movement.CashierName
        : $"{Movement.CashierName} / {Movement.AuthorizedByName}";

    public string NoteText => Movement.Note ?? string.Empty;

    public string UploadText => translate(Movement.UploadStatus switch
    {
        LocalCashMovementUploadStatus.Synced => "cashMovement.records.synced",
        LocalCashMovementUploadStatus.Rejected => "cashMovement.records.rejected",
        _ => "cashMovement.records.pending"
    });

    /// <summary>冲正记录、已被冲正的原记录都不能再冲正；其余条件（今天、日结前）由服务再校验。</summary>
    public bool CanVoid => !Movement.IsVoid && !IsVoided;
}
