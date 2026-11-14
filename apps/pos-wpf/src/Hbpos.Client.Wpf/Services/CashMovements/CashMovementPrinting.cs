using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

public interface ICashMovementPrintService
{
    Task<ReceiptPrintResult> PrintAsync(
        LocalCashMovement movement,
        LocalCashMovement? voidedOriginal,
        DailyCloseCashMovementTotals periodTotals,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);
}

/// <summary>现金进出凭条：每笔打 1 张（2026-11-13 已定口径），放进钱箱给店长核对。</summary>
public sealed class CashMovementPrintService(
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptPrinterDriver driver) : ICashMovementPrintService
{
    public async Task<ReceiptPrintResult> PrintAsync(
        LocalCashMovement movement,
        LocalCashMovement? voidedOriginal,
        DailyCloseCashMovementTotals periodTotals,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var result = await driver.PrintAsync(
            CashMovementTextFormatter.Build(movement, voidedOriginal, periodTotals, settings, reason),
            settings,
            cancellationToken);
        return new ReceiptPrintResult(result.Succeeded, result.Message);
    }
}

/// <summary>凭条排版：抬头 → 标题 → 明细 → 本期合计 → 签名栏。英文，与日结小票同一口径。</summary>
public static class CashMovementTextFormatter
{
    private const int LineWidth = 42;

    public static ReceiptPrintDocument Build(
        LocalCashMovement movement,
        LocalCashMovement? voidedOriginal,
        DailyCloseCashMovementTotals periodTotals,
        ReceiptPrinterSettings settings,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual)
    {
        var elements = new List<ReceiptPrintElement>();
        var rows = new List<ReceiptPreviewRow>();

        void Text(string text, ReceiptPrintAlignment alignment = ReceiptPrintAlignment.Left, bool emphasized = false)
        {
            elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Text, text, alignment, emphasized));
            rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, text, alignment, emphasized));
        }

        void Separator()
        {
            var line = new string('-', LineWidth);
            elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Separator, line));
            rows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, line));
        }

        var header = new[] { settings.BrandName, settings.StoreName, movement.StoreCode }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? movement.StoreCode;
        Text(header, ReceiptPrintAlignment.Center, emphasized: true);
        Text(string.Empty);

        var title = movement.IsVoid
            ? "CASH MOVEMENT VOID"
            : movement.Direction == CashMovementDirection.In ? "CASH IN" : "CASH OUT";
        if (reason == ReceiptPrintReason.Reprint)
        {
            title += " REPRINT";
        }

        Text($"==== {title} ====", ReceiptPrintAlignment.Center, emphasized: true);
        Separator();
        if (voidedOriginal is not null)
        {
            Text(TwoColumns("Voids", voidedOriginal.DisplayNumber));
            Text(TwoColumns("Original", $"{ReasonLabel(voidedOriginal.ReasonCode)} {Signed(voidedOriginal.SignedAmount)}"));
        }
        else
        {
            Text(TwoColumns("Reason", ReasonLabel(movement.ReasonCode)));
        }

        Text(TwoColumns("Amount", Signed(movement.SignedAmount)), emphasized: true);
        if (!string.IsNullOrWhiteSpace(movement.Note))
        {
            foreach (var line in Wrap($"Note: {movement.Note}", LineWidth))
            {
                Text(line);
            }
        }

        Separator();
        Text(TwoColumns("Till", movement.DeviceCode));
        Text(TwoColumns("Cashier", movement.CashierName));
        if (!string.IsNullOrWhiteSpace(movement.AuthorizedByName))
        {
            Text(TwoColumns("Authorised", movement.AuthorizedByName));
        }

        Text(TwoColumns("Time", movement.OccurredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)));
        Text(TwoColumns("Ref", movement.DisplayNumber));
        Separator();
        Text(TwoColumns("Today cash in", DailyCloseMoneyFormat.SignedMoney(periodTotals.CashIn)));
        Text(TwoColumns("Today cash out", DailyCloseMoneyFormat.SignedMoney(-periodTotals.CashOut)));
        Text(string.Empty);
        Text("Cashier ______________________");
        if (movement.ReasonCode == CashMovementReasons.SafeDrop && !movement.IsVoid)
        {
            // 交店长：多一栏给店长签收。
            Text(string.Empty);
            Text("Received by __________________");
        }

        Text(string.Empty);
        Text("KEEP THIS SLIP IN THE DRAWER", ReceiptPrintAlignment.Center);
        Text(string.Empty);
        return new ReceiptPrintDocument(elements, rows);
    }

    public static string ReasonLabel(string reasonCode) => reasonCode switch
    {
        CashMovementReasons.ChangeTopUp => "Change top-up",
        CashMovementReasons.OtherIn => "Other cash in",
        CashMovementReasons.SafeDrop => "Safe drop",
        CashMovementReasons.PaidOut => "Paid out",
        _ => reasonCode
    };

    private static string Signed(decimal amount) => DailyCloseMoneyFormat.SignedMoney(amount);

    private static string TwoColumns(string left, string right)
    {
        right ??= string.Empty;
        if (right.Length > 26)
        {
            right = right[..23] + "...";
        }

        return left + new string(' ', Math.Max(1, LineWidth - left.Length - right.Length)) + right;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        for (var index = 0; index < text.Length; index += width)
        {
            yield return text.Substring(index, Math.Min(width, text.Length - index));
        }
    }
}
