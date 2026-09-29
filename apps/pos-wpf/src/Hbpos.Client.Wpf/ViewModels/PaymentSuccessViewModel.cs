using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class PaymentSuccessViewModel : ObservableObject
{
    private readonly IReceiptQueryService? _receiptQueryService;
    private readonly IReceiptTextFormatter _receiptTextFormatter;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;

    [ObservableProperty]
    private Guid? _transactionId;

    [ObservableProperty]
    private decimal _totalAmountPaid;

    [ObservableProperty]
    private DateTimeOffset? _soldAt;

    [ObservableProperty]
    private string _storeCode = string.Empty;

    [ObservableProperty]
    private string _deviceCode = string.Empty;

    [ObservableProperty]
    private string _cashierName = string.Empty;

    [ObservableProperty]
    private decimal? _tenderedAmount;

    [ObservableProperty]
    private decimal? _changeAmount;

    [ObservableProperty]
    private bool _isCashChangeVisible;

    [ObservableProperty]
    private string? _cardPaymentSummary;

    [ObservableProperty]
    private bool _isCardPaymentSummaryVisible;

    [ObservableProperty]
    private bool _hasPostCommitWarning;

    public PaymentSuccessViewModel()
        : this(initialize: true, receiptQueryService: null, receiptTextFormatter: null, receiptPrinterSettingsStore: null)
    {
    }

    public PaymentSuccessViewModel(ILocalOrderRepository orderRepository)
        : this(initialize: true, receiptQueryService: new ReceiptQueryService(orderRepository), receiptTextFormatter: null, receiptPrinterSettingsStore: null)
    {
    }

    public PaymentSuccessViewModel(
        IReceiptQueryService receiptQueryService,
        IReceiptTextFormatter? receiptTextFormatter = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null)
        : this(initialize: true, receiptQueryService, receiptTextFormatter, receiptPrinterSettingsStore)
    {
    }

    private PaymentSuccessViewModel(
        bool initialize,
        IReceiptQueryService? receiptQueryService,
        IReceiptTextFormatter? receiptTextFormatter,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore)
    {
        _receiptQueryService = receiptQueryService;
        _receiptTextFormatter = receiptTextFormatter ?? new ReceiptTextFormatter();
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        PrintReceiptCommand = new RelayCommand(() => PrintReceiptRequested?.Invoke(this, EventArgs.Empty), () => TransactionId is not null);
        NewTransactionCommand = new RelayCommand(() => NewTransactionRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? PrintReceiptRequested;

    public event EventHandler? NewTransactionRequested;

    public ObservableCollection<ReceiptPreviewLine> ReceiptLines { get; } = [];

    public ObservableCollection<ReceiptPaymentLine> Payments { get; } = [];

    public ObservableCollection<ReceiptPreviewRow> ReceiptPreviewRows { get; } = [];

    public IRelayCommand PrintReceiptCommand { get; }

    public IRelayCommand NewTransactionCommand { get; }

    public string TitleText => "PaymentSuccessful";

    public string SubtitleText => "success.subtitle";

    public string TotalAmountPaidLabel => "success.totalPaid";

    public string TransactionIdLabel => "success.transactionId";

    public string ReceiptPreviewLabel => "success.receiptPreview";

    public string PrintReceiptLabel => "success.printReceipt";

    public string NewTransactionLabel => "success.newTransaction";

    // 中文注释：金额固定按 $ 显示，不随界面语言变成其它货币符号。
    public string TotalAmountPaidDisplay => FormatMoney(TotalAmountPaid);

    public string TenderedAmountDisplay => TenderedAmount is { } amount ? FormatMoney(amount) : "-";

    public string ChangeAmountDisplay => ChangeAmount is { } amount ? FormatMoney(amount) : "-";

    public string TransactionIdDisplay => TransactionId is null
        ? "-"
        : $"#{TransactionId.Value.ToString("N")[..10].ToUpperInvariant()}";

    public string SoldAtDisplay => SoldAt?.ToLocalTime().ToString("MMM dd, yyyy HH:mm") ?? "-";

    public decimal Subtotal => ReceiptLines.Sum(line => line.ActualAmount);

    public decimal DiscountTotal => ReceiptLines.Sum(line => line.DiscountAmount);

    public async Task LoadAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        if (_receiptQueryService is null)
        {
            return;
        }

        var receipt = await _receiptQueryService.GetReceiptAsync(orderGuid, cancellationToken);
        if (receipt is not null)
        {
            ApplyReceipt(receipt, await LoadPreviewSettingsAsync(cancellationToken));
        }
    }

    public async Task LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        if (_receiptQueryService is null)
        {
            return;
        }

        var latest = await _receiptQueryService.GetLatestReceiptAsync(cancellationToken);
        if (latest is not null)
        {
            ApplyReceipt(latest, await LoadPreviewSettingsAsync(cancellationToken));
        }
    }

    public void LoadFromOrder(LocalOrder order)
    {
        ApplyReceipt(ReceiptQueryService.CreateReceipt(order), ReceiptPrinterSettings.Default);
    }

    public async Task LoadFromOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
    {
        ApplyReceipt(
            ReceiptQueryService.CreateReceipt(order),
            await LoadPreviewSettingsAsync(cancellationToken));
    }

    private void ApplyReceipt(ReceiptDetails receipt, ReceiptPrinterSettings settings)
    {
        TransactionId = receipt.OrderGuid;
        TotalAmountPaid = receipt.ActualAmount;
        SoldAt = receipt.SoldAt;
        StoreCode = receipt.StoreCode;
        DeviceCode = receipt.DeviceCode;
        CashierName = receipt.CashierName;
        TenderedAmount = receipt.TenderedAmount;
        ChangeAmount = receipt.ChangeAmount;
        IsCashChangeVisible = ShouldShowCashChange(receipt);
        CardPaymentSummary = GetCardPaymentSummary(receipt);
        IsCardPaymentSummaryVisible = !string.IsNullOrWhiteSpace(CardPaymentSummary);

        ReceiptLines.ReplaceWith(receipt.Lines);
        Payments.ReplaceWith(receipt.Payments);
        ReceiptPreviewRows.ReplaceWith(BuildPreviewRows(receipt, settings));

        OnPropertyChanged(nameof(TransactionIdDisplay));
        OnPropertyChanged(nameof(TotalAmountPaidDisplay));
        OnPropertyChanged(nameof(SoldAtDisplay));
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(TenderedAmountDisplay));
        OnPropertyChanged(nameof(ChangeAmountDisplay));
        PrintReceiptCommand.NotifyCanExecuteChanged();
    }

    private async Task<ReceiptPrinterSettings> LoadPreviewSettingsAsync(CancellationToken cancellationToken)
    {
        if (_receiptPrinterSettingsStore is null)
        {
            return ReceiptPrinterSettings.Default;
        }

        try
        {
            return await _receiptPrinterSettingsStore.LoadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReceiptPrinterSettings.Default;
        }
    }

    private IReadOnlyList<ReceiptPreviewRow> BuildPreviewRows(ReceiptDetails receipt, ReceiptPrinterSettings settings)
    {
        try
        {
            return AddCashChangePreviewRows(_receiptTextFormatter.Build(receipt, settings, receipt.SoldAt).PreviewRows, receipt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                return AddCashChangePreviewRows(new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt).PreviewRows, receipt);
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                return [];
            }
        }
    }

    private static bool ShouldShowCashChange(ReceiptDetails receipt)
    {
        // 旧订单没有持久化找零字段时不推测，避免历史交易显示错误找零。
        return receipt.ActualAmount > 0m &&
            receipt.ChangeAmount is not null &&
            receipt.Payments.Any(payment => payment.Method == PaymentMethodKind.Cash);
    }

    private static string? GetCardPaymentSummary(ReceiptDetails receipt)
    {
        // 成功页只展示已经脱敏的本地卡交易摘要，不从原始 Square 响应推导任何敏感字段。
        return receipt.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Card)
            .Select(payment => payment.CardSummary)
            .FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary))?
            .Trim();
    }

    private static IReadOnlyList<ReceiptPreviewRow> AddCashChangePreviewRows(
        IReadOnlyList<ReceiptPreviewRow> rows,
        ReceiptDetails receipt)
    {
        if (!ShouldShowCashChange(receipt))
        {
            return rows;
        }

        var previewRows = rows.ToList();
        previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, new string('-', 42)));
        // 这里只增强成功页预览，不写入 ReceiptTextFormatter，因此不会改变实际打印小票。
        previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns("Tendered", FormatMoney(receipt.TenderedAmount ?? 0m)), ReceiptPrintAlignment.Left, true));
        previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, FitPreviewColumns("Change", FormatMoney(receipt.ChangeAmount ?? 0m)), ReceiptPrintAlignment.Left, true));
        return previewRows;
    }

    private static string FormatMoney(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string FitPreviewColumns(string left, string right)
    {
        const int lineWidth = 42;
        left = left.Length > 24 ? left[..24] : left;
        right = right.Length > 16 ? right[..16] : right;
        return left + new string(' ', Math.Max(1, lineWidth - left.Length - right.Length)) + right;
    }
}
