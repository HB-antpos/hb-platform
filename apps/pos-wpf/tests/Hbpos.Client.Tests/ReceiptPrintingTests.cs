using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using System.Globalization;

namespace Hbpos.Client.Tests;

public sealed class ReceiptPrintingTests
{
    [Fact]
    public void Installment_receipt_mapper_builds_deposit_receipt_document()
    {
        var depositTime = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero);
        var repaymentTime = new DateTimeOffset(2026, 7, 4, 12, 45, 0, TimeSpan.Zero);
        var order = new LocalInstallmentOrder(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "IO-20260704-0001",
            "S001",
            "POS-01",
            "user-1",
            "Alice",
            "Bob Buyer",
            "0400111222",
            depositTime,
            new DateTimeOffset(2026, 7, 4, 12, 31, 0, TimeSpan.Zero),
            80m,
            20m,
            20m,
            35m,
            45m,
            InstallmentStatus.Active,
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-INST",
                    null,
                    "Installment Tea",
                    "939003",
                    1m,
                    80m,
                    0m,
                    80m,
                    "ITEM-INST")
            ],
            [
                new InstallmentPaymentDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Cash,
                    20m,
                    "CASH",
                    InstallmentPaymentStatus.Recorded,
                    depositTime,
                    "user-1",
                    "POS-01"),
                new InstallmentPaymentDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Card,
                    15m,
                    "CARD-REF",
                    InstallmentPaymentStatus.Recorded,
                    repaymentTime,
                    "user-1",
                    "POS-01")
            ],
            null);
        var formatter = new ReceiptTextFormatter();
        var settings = ReceiptPrinterSettings.Default with { StoreName = "Main Store" };

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);
        var document = formatter.Build(receipt, settings, order.CreatedAt);

        var detailLine = Assert.Single(receipt.Lines);
        Assert.Equal("SKU-INST", detailLine.ProductCode);
        Assert.Equal("ITEM-INST", detailLine.ItemNumber);

        Assert.DoesNotContain("INSTALLMENT ORDER", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Installment No: IO-20260704-0001", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("IO-20260704-0001", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Bob Buyer", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("0400111222", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Deposit paid: $20.00", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Balance due: $45.00", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Payment history:", document.PlainText, StringComparison.Ordinal);
        Assert.Contains($"{depositTime.ToLocalTime():yyyy-MM-dd HH:mm} Cash $20.00", document.PlainText, StringComparison.Ordinal);
        Assert.Contains($"{repaymentTime.ToLocalTime():yyyy-MM-dd HH:mm} Card $15.00", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Ref: CARD-REF", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Installment Tea", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Cash", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Store: Main Store (S001)", document.PlainText, StringComparison.Ordinal);
        Assert.Contains(document.Elements, element => element.Text == "Store: Main Store (S001)");
        Assert.Contains(document.PreviewRows, row => row.Text == "Store: Main Store (S001)");
    }

    [Fact]
    public void Installment_receipt_mapper_prints_pending_pickup_for_paid_off_order()
    {
        var order = CreateInstallmentOrder(
            InstallmentStatus.PaidOff,
            paidAmount: 80m,
            balanceAmount: 0m);
        var formatter = new ReceiptTextFormatter();

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);
        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, order.CreatedAt);

        Assert.Equal("*** Paid - Pickup Pending ***", receipt.StatusText);
        Assert.Contains("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTALLMENT ORDER", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Installment No: IO-20260704-0002", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Balance due: $0.00", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Pickup: Pending", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Installment_refund_vouchers_are_read_in_order_and_each_prints_a_standalone_voucher_document()
    {
        var baseOrder = CreateInstallmentOrder(InstallmentStatus.Cancelled, paidAmount: 0m, balanceAmount: 0m);
        var at = baseOrder.CreatedAt;
        InstallmentPaymentDto Payment(PaymentMethodKind method, decimal amount, string? reference, int minutes, InstallmentPaymentStatus status = InstallmentPaymentStatus.Recorded) =>
            new(Guid.NewGuid(), method, amount, reference, status, at.AddMinutes(minutes), "user-1", "POS-01");
        var order = baseOrder with
        {
            Payments =
            [
                Payment(PaymentMethodKind.Card, 30m, "ANZ:TXN-1", 0),
                Payment(PaymentMethodKind.Cash, 20m, null, 1),
                Payment(PaymentMethodKind.Voucher, -20m, "VOUCHER_REFUND:RF-0002", 6),
                Payment(PaymentMethodKind.Voucher, -30m, "VOUCHER_REFUND:RF-0001", 5),
                // 原代金券付款、已作废的退款、非退款券引用都不是本次签发的退款券。
                Payment(PaymentMethodKind.Voucher, 10m, "VOUCHER:VC-1:TOKEN", 2),
                Payment(PaymentMethodKind.Voucher, -10m, "VOUCHER_REFUND:RF-VOID", 7, InstallmentPaymentStatus.Voided),
                Payment(PaymentMethodKind.Voucher, -10m, "VOUCHER_REFUND_PENDING", 8)
            ]
        };

        var vouchers = InstallmentReceiptMapper.GetRefundVouchers(order);

        Assert.Equal(
            [new RefundVoucherReceipt("RF-0001", 30m), new RefundVoucherReceipt("RF-0002", 20m)],
            vouchers);

        var receipt = InstallmentReceiptMapper.CreateRefundVoucherReceipt(order, vouchers[0]);
        var payment = Assert.Single(receipt.Payments);
        Assert.Equal(PaymentMethodKind.Voucher, payment.Method);
        Assert.Null(payment.CardTransactions);
        Assert.Equal(vouchers[0], receipt.RefundVoucher);
        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, null);
        Assert.Contains("REFUND VOUCHER", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Voucher: RF-0001", document.PlainText, StringComparison.Ordinal);
        Assert.Contains($"Order: {order.InstallmentNumber}", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("RF-0002", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Installment_receipt_mapper_does_not_mark_cancelled_zero_balance_order_as_pickup_pending()
    {
        var order = CreateInstallmentOrder(
            InstallmentStatus.Cancelled,
            paidAmount: 0m,
            balanceAmount: 0m);
        var formatter = new ReceiptTextFormatter();

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);
        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, order.CreatedAt);

        Assert.Equal("*** Installment Cancelled ***", receipt.StatusText);
        Assert.DoesNotContain("Pickup: Pending", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Paid - Pickup Pending", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Installment_receipt_mapper_prints_confirmed_pickup_details()
    {
        var pickedUpAt = new DateTimeOffset(2026, 7, 4, 13, 0, 0, TimeSpan.Zero);
        var order = CreateInstallmentOrder(
            InstallmentStatus.PickedUp,
            paidAmount: 80m,
            balanceAmount: 0m,
            pickupInfo: new InstallmentPickupInfoDto(pickedUpAt, "Alice", "Customer collected at counter"));
        var formatter = new ReceiptTextFormatter();

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);
        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, order.CreatedAt);

        Assert.Equal("*** Paid - Picked Up ***", receipt.StatusText);
        Assert.Contains("Pickup: Confirmed", document.PlainText, StringComparison.Ordinal);
        Assert.Contains($"Picked up at: {pickedUpAt.ToLocalTime():yyyy-MM-dd HH:mm}", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Picked up by: Alice", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Pickup note: Customer collected at counter", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Installment_receipt_for_active_order_prints_terms_after_payments_and_before_barcode()
    {
        var order = CreateInstallmentOrder(InstallmentStatus.Active, paidAmount: 20m, balanceAmount: 60m);

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);
        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, order.CreatedAt);

        Assert.Equal("INSTALLMENT TERMS", receipt.Terms?.Title);
        Assert.Contains("INSTALLMENT TERMS", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Order total: $50.00 minimum.", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("First payment: $20.00 minimum.", document.PlainText, StringComparison.Ordinal);
        // 第三条超过一行纸宽会被自动换行，所以按「折叠空白后的整句」断言，不绑定具体断行位置。
        var collapsed = System.Text.RegularExpressions.Regex.Replace(document.PlainText, @"\s+", " ");
        Assert.Contains(
            "Each later payment: $5.00 minimum, or the remaining balance if it is lower.",
            collapsed,
            StringComparison.Ordinal);
        // 位置必须用元素序列判断：PlainText 不含条码文本，只比较 Print Time 抓不到「条款被挪到条码之后」。
        var elements = document.Elements;
        var paymentAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "Payment:");
        var termsAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "INSTALLMENT TERMS");
        var barcodeAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Barcode);
        Assert.True(paymentAt >= 0 && paymentAt < termsAt, "条款必须在付款明细之后");
        Assert.True(termsAt < barcodeAt, "条款必须在条码之前");
        // 条款前有分隔线，标题居中加粗（与同位置的 Refunds and returns 一致）。
        Assert.Equal(ReceiptPrintElementKind.Separator, elements[termsAt - 1].Kind);
        Assert.Equal(ReceiptPrintAlignment.Center, elements[termsAt].Alignment);
        Assert.True(elements[termsAt].IsEmphasized);
        // 条款块自身（标题到下一条分隔线之间）每行都不超过 42 字符纸宽；
        // 不对整张小票断言宽度：条码预览行 "BARCODE <36 位订单号>" 本身就是 44 字符，与条款无关。
        var termsEnd = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Separator, termsAt + 1);
        Assert.All(
            elements.Skip(termsAt).Take(termsEnd - termsAt),
            element => Assert.True(element.Text.Length <= 42, element.Text));
    }

    [Fact]
    public void Installment_receipt_for_active_order_with_pickup_info_does_not_print_terms()
    {
        // 进行中却带提货信息属于不一致数据，与手持/iPad 的判断一致，一律不打印条款。
        var order = CreateInstallmentOrder(
            InstallmentStatus.Active,
            paidAmount: 20m,
            balanceAmount: 60m,
            pickupInfo: new InstallmentPickupInfoDto(
                new DateTimeOffset(2026, 7, 4, 13, 0, 0, TimeSpan.Zero),
                "Alice",
                "Customer collected at counter"));

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);

        Assert.Null(receipt.Terms);
    }

    private static int IndexOfElement(
        IReadOnlyList<ReceiptPrintElement> elements,
        Func<ReceiptPrintElement, bool> predicate,
        int startIndex = 0)
    {
        for (var index = startIndex; index < elements.Count; index++)
        {
            if (predicate(elements[index]))
            {
                return index;
            }
        }

        return -1;
    }

    [Theory]
    [InlineData(InstallmentStatus.PaidOff)]
    [InlineData(InstallmentStatus.PickedUp)]
    [InlineData(InstallmentStatus.Cancelled)]
    public void Installment_receipt_for_finished_order_does_not_print_terms(InstallmentStatus status)
    {
        var order = CreateInstallmentOrder(status, paidAmount: 0m, balanceAmount: 0m);

        var receipt = InstallmentReceiptMapper.CreateReceipt(order);
        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, order.CreatedAt);

        Assert.Null(receipt.Terms);
        Assert.DoesNotContain("INSTALLMENT TERMS", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Installment_terms_do_not_leak_into_standalone_voucher_documents_or_regular_receipts()
    {
        var order = CreateInstallmentOrder(InstallmentStatus.Active, paidAmount: 20m, balanceAmount: 60m);
        var installmentReceipt = InstallmentReceiptMapper.CreateReceipt(order);
        var formatter = new ReceiptTextFormatter();

        // 即使带条款的小票被派生成退款券/余额凭证，独立券面也不能打印分期条款。
        var refundVoucherDocument = formatter.Build(
            installmentReceipt with { RefundVoucher = new RefundVoucherReceipt("RF-TEST-1", 5m) },
            ReceiptPrinterSettings.Default,
            order.CreatedAt);
        var balanceDocument = formatter.Build(
            installmentReceipt with { VoucherBalance = new VoucherBalanceReceipt("VC-TEST-1", 7m) },
            ReceiptPrinterSettings.Default,
            order.CreatedAt);
        var regularDocument = formatter.Build(
            CreateReceipt(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            ReceiptPrinterSettings.Default,
            order.CreatedAt);

        Assert.DoesNotContain("INSTALLMENT TERMS", refundVoucherDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTALLMENT TERMS", balanceDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTALLMENT TERMS", regularDocument.PlainText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 总部定制的条款正文

    private static readonly string[] DefaultInstallmentTermLines =
    [
        "Order total: $50.00 minimum.",
        "First payment: $20.00 minimum.",
        "Each later payment: $5.00 minimum, or the remaining balance if it is lower."
    ];

    private static readonly string[] DefaultVoucherTermLines =
    [
        "Use at the issuing store only.",
        "Pay with it at checkout by scanning the barcode or QR code.",
        "Can be used across several purchases until the balance is $0.00.",
        "Not redeemable for cash."
    ];

    /// <summary>取出某个条款块的正文：标题之后到下一条分隔线（或文档结束）之间的所有文本行。</summary>
    private static List<string> TermBodyLines(ReceiptPrintDocument document, string title)
    {
        var elements = document.Elements;
        var titleAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == title);
        Assert.True(titleAt >= 0, $"小票里没有条款标题 {title}");
        var end = IndexOfElement(
            elements,
            element => element.Kind is ReceiptPrintElementKind.Separator or ReceiptPrintElementKind.Barcode or ReceiptPrintElementKind.QrCode,
            titleAt + 1);
        var stop = end < 0 ? elements.Count : end;
        return elements.Skip(titleAt + 1).Take(stop - titleAt - 1)
            .Where(element => element.Kind == ReceiptPrintElementKind.Text && element.Text.Length > 0)
            .Select(element => element.Text)
            .ToList();
    }

    private static ReceiptDetails ActiveInstallmentReceipt() =>
        InstallmentReceiptMapper.CreateReceipt(
            CreateInstallmentOrder(InstallmentStatus.Active, paidAmount: 20m, balanceAmount: 60m));

    private static ReceiptDetails RefundVoucherReceipt(string code = "RF123")
    {
        return CreateReceipt(Guid.NewGuid(), paymentReference: $"VOUCHER_REFUND:{code}", paymentMethod: PaymentMethodKind.Voucher) with
        {
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, $"VOUCHER_REFUND:{code}")],
            RefundVoucher = new RefundVoucherReceipt(code, 8m)
        };
    }

    [Fact]
    public void Installment_terms_without_customization_print_the_exact_default_wording()
    {
        var document = new ReceiptTextFormatter().Build(ActiveInstallmentReceipt(), ReceiptPrinterSettings.Default);

        var body = TermBodyLines(document, "INSTALLMENT TERMS");
        // 默认文案（含自动换行）与改动前逐字一致：折叠空白后能拼回三条原句。
        var collapsed = System.Text.RegularExpressions.Regex.Replace(string.Join(" ", body), @"\s+", " ");
        Assert.Equal(string.Join(" ", DefaultInstallmentTermLines), collapsed);
    }

    [Fact]
    public void Installment_terms_print_the_customized_body_line_by_line_and_drop_blank_lines()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            InstallmentTerms = "  Deposit is non-refundable.  \r\n\r\n   \nBalance due within 30 days.\rPick up with this slip."
        };

        var document = new ReceiptTextFormatter().Build(ActiveInstallmentReceipt(), settings);

        Assert.Equal(
            ["Deposit is non-refundable.", "Balance due within 30 days.", "Pick up with this slip."],
            TermBodyLines(document, "INSTALLMENT TERMS"));
        // 定制后默认英文稿不再出现，标题仍由收银端固定印。
        Assert.DoesNotContain("Order total: $50.00 minimum.", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Installment_terms_keep_their_position_after_return_policy_and_before_the_barcode_when_customized()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            ReturnPolicy = "Return within 7 days",
            InstallmentTerms = "Custom installment line."
        };

        var document = new ReceiptTextFormatter().Build(ActiveInstallmentReceipt(), settings);

        var elements = document.Elements;
        var policyAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "Refunds and returns");
        var termsAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "INSTALLMENT TERMS");
        var barcodeAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Barcode);
        Assert.True(policyAt >= 0 && policyAt < termsAt && termsAt < barcodeAt);
        Assert.Equal(ReceiptPrintElementKind.Separator, elements[termsAt - 1].Kind);
        Assert.Equal(ReceiptPrintAlignment.Center, elements[termsAt].Alignment);
        Assert.True(elements[termsAt].IsEmphasized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n \n\t\r")]
    public void Blank_installment_terms_fall_back_to_the_default_wording(string custom)
    {
        var settings = ReceiptPrinterSettings.Default with { InstallmentTerms = custom };

        var document = new ReceiptTextFormatter().Build(ActiveInstallmentReceipt(), settings);

        Assert.Contains("Order total: $50.00 minimum.", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("First payment: $20.00 minimum.", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Customized_installment_terms_wrap_long_and_chinese_lines_within_paper_width()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            InstallmentTerms = "This is a rather long customised sentence that must be wrapped to the paper width by words.\n"
                + "定金不可退还，请在三十天内付清余款并凭此小票提货，逾期视为放弃，门店不另行通知顾客本人。"
        };

        var document = new ReceiptTextFormatter().Build(ActiveInstallmentReceipt(), settings);

        var body = TermBodyLines(document, "INSTALLMENT TERMS");
        Assert.True(body.Count > 2, "两条长句都应被拆成多行");
        // 显示宽度按「中日韩字符占两列」计，每一行都不能超过 42 列纸宽。
        Assert.All(body, line => Assert.True(DisplayColumns(line) <= 42, line));
    }

    private static int DisplayColumns(string line) =>
        line.EnumerateRunes().Sum(rune => rune.Value is >= 0x2E80 and <= 0xA4CF or >= 0xFF00 and <= 0xFF60 ? 2 : 1);

    [Fact]
    public void Customized_installment_terms_do_not_change_finished_or_regular_receipts()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            InstallmentTerms = "Custom installment line.",
            VoucherTerms = "Custom voucher line."
        };
        var finished = InstallmentReceiptMapper.CreateReceipt(
            CreateInstallmentOrder(InstallmentStatus.PaidOff, paidAmount: 0m, balanceAmount: 0m));
        var formatter = new ReceiptTextFormatter();

        var finishedDocument = formatter.Build(finished, settings);
        var regularDocument = formatter.Build(CreateReceipt(Guid.NewGuid()), settings);

        // 已付清 / 普通小票本来就没有条款块，定制正文不能凭空多印出来。
        Assert.DoesNotContain("INSTALLMENT TERMS", finishedDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom installment line.", finishedDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom installment line.", regularDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom voucher line.", regularDocument.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Refund_voucher_terms_without_customization_print_the_exact_default_wording()
    {
        var document = new ReceiptTextFormatter().Build(RefundVoucherReceipt(), ReceiptPrinterSettings.Default);

        var collapsed = System.Text.RegularExpressions.Regex.Replace(
            string.Join(" ", TermBodyLines(document, "VOUCHER TERMS")), @"\s+", " ");
        Assert.Equal(string.Join(" ", DefaultVoucherTermLines), collapsed);
    }

    [Fact]
    public void Refund_voucher_terms_print_the_customized_body_below_the_codes()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            VoucherTerms = "Valid at any store.\n\n  No cash back.  \r\nShow this slip at checkout."
        };

        var document = new ReceiptTextFormatter().Build(RefundVoucherReceipt(), settings);

        Assert.Equal(
            ["Valid at any store.", "No cash back.", "Show this slip at checkout."],
            TermBodyLines(document, "VOUCHER TERMS"));
        Assert.DoesNotContain("Use at the issuing store only.", document.PlainText, StringComparison.Ordinal);
        // 位置不变：使用说明仍在条码 / 二维码之后。
        var elements = document.Elements;
        var qrAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.QrCode);
        var termsAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "VOUCHER TERMS");
        Assert.True(qrAt >= 0 && qrAt < termsAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void Blank_voucher_terms_fall_back_to_the_default_wording(string custom)
    {
        var settings = ReceiptPrinterSettings.Default with { VoucherTerms = custom };

        var document = new ReceiptTextFormatter().Build(RefundVoucherReceipt(), settings);

        Assert.Contains("Not redeemable for cash.", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Customized_voucher_terms_and_installment_terms_do_not_bleed_into_each_other()
    {
        var formatter = new ReceiptTextFormatter();
        var onlyVoucher = ReceiptPrinterSettings.Default with { VoucherTerms = "Voucher only line." };
        var onlyInstallment = ReceiptPrinterSettings.Default with { InstallmentTerms = "Installment only line." };

        var installmentWithVoucherCustom = formatter.Build(ActiveInstallmentReceipt(), onlyVoucher);
        var voucherWithInstallmentCustom = formatter.Build(RefundVoucherReceipt(), onlyInstallment);
        var balanceDocument = formatter.Build(
            CreateReceipt(Guid.NewGuid()) with { VoucherBalance = new VoucherBalanceReceipt("VC200", 12.34m) },
            onlyVoucher with { InstallmentTerms = "Installment only line." });

        Assert.Contains("Order total: $50.00 minimum.", installmentWithVoucherCustom.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Voucher only line.", installmentWithVoucherCustom.PlainText, StringComparison.Ordinal);
        Assert.Contains("Not redeemable for cash.", voucherWithInstallmentCustom.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Installment only line.", voucherWithInstallmentCustom.PlainText, StringComparison.Ordinal);
        // 余额凭证一直没有使用说明块，定制正文也不能让它多出来。
        Assert.DoesNotContain("VOUCHER TERMS", balanceDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Voucher only line.", balanceDocument.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_print_service_applies_customized_terms_on_every_render_path()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            VoucherTerms = "Printed voucher custom.",
            InstallmentTerms = "Printed installment custom."
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore { Settings = settings },
            new ReceiptTextFormatter(),
            driver);

        // 现场出票与重打印 / 远程历史补打都是「拿到 ReceiptDetails 再交给同一个格式化器」，取值逻辑只有一份。
        await service.PrintReceiptAsync(RefundVoucherReceipt(), ReceiptPrintReason.VoucherRefundAuto);
        Assert.Contains("Printed voucher custom.", driver.LastDocument!.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Use at the issuing store only.", driver.LastDocument.PlainText, StringComparison.Ordinal);

        await service.PrintReceiptAsync(ActiveInstallmentReceipt(), ReceiptPrintReason.Reprint);
        Assert.Contains("Printed installment custom.", driver.LastDocument.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Order total: $50.00 minimum.", driver.LastDocument.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_with_old_snapshot_without_terms_prints_default_wording_end_to_end()
    {
        // 旧快照 / 升级前的本机库：设置里没有这两个键，从存储读出再打印，必须与改动前的小票逐字一致。
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository);
        await store.SaveAsync(ReceiptPrinterSettings.Default with { ReturnPolicy = "Return within 7 days" });
        var settings = await store.LoadAsync();
        var formatter = new ReceiptTextFormatter();
        // 固定打印时间并复用同一张小票：避免 Print Time / 随机订单号让两次渲染天然不同。
        var printTime = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        var installment = ActiveInstallmentReceipt();
        var voucher = RefundVoucherReceipt("RF9");

        var withLoaded = formatter.Build(installment, settings, printTime);
        var withDefault = formatter.Build(
            installment,
            ReceiptPrinterSettings.Default with { ReturnPolicy = "Return within 7 days" },
            printTime);

        Assert.Equal(string.Empty, settings.InstallmentTerms);
        Assert.Equal(string.Empty, settings.VoucherTerms);
        Assert.Equal(withDefault.PlainText, withLoaded.PlainText);
        Assert.Equal(
            formatter.Build(voucher, ReceiptPrinterSettings.Default, printTime).PlainText,
            formatter.Build(voucher, settings, printTime).PlainText);
    }

    [Fact]
    public void Receipt_text_formatter_builds_print_commands_and_preview_from_same_document()
    {
        var orderGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var receipt = CreateReceipt(orderGuid);
        var settings = new ReceiptPrinterSettings(
            "USB,",
            "HotBargain",
            "Main Store",
            "1 Main Street Brisbane",
            "07 3000 0000",
            "12 345 678 901",
            "Keep receipt for refunds.",
            60);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(
            receipt,
            settings,
            new DateTimeOffset(2026, 5, 27, 10, 30, 0, TimeSpan.Zero));

        Assert.Contains(document.PreviewRows, row => row.Text == "HotBargain" && row.IsEmphasized && row.IsCentered);
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("===== TAX INVOICE =====", StringComparison.Ordinal));
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("Organic Gala Apples", StringComparison.Ordinal));
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("GST", StringComparison.Ordinal));
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("APPROVED CARD RECEIPT", StringComparison.Ordinal));
        Assert.Contains(document.Elements, element => element.Text == "Store: Main Store (S001)");
        Assert.Contains(document.PreviewRows, row => row.Text == "Store: Main Store (S001)");
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == orderGuid.ToString());
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.QrCode && element.Text == orderGuid.ToString());
        var qrPreview = Assert.Single(document.PreviewRows, row => row.IsQrCode);
        Assert.Equal(orderGuid.ToString("D"), qrPreview.QrCodeValue);
    }

    [Fact]
    public void Receipt_text_formatter_falls_back_to_trimmed_store_code_when_store_name_is_blank()
    {
        var receipt = CreateReceipt(Guid.NewGuid()) with { StoreCode = "  S001  " };
        var settings = ReceiptPrinterSettings.Default with { StoreName = "   " };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, settings, receipt.SoldAt);

        Assert.Contains(document.Elements, element => element.Text == "Store: S001");
    }

    [Fact]
    public void Receipt_text_formatter_does_not_repeat_store_name_when_it_matches_store_code()
    {
        var receipt = CreateReceipt(Guid.NewGuid());
        var settings = ReceiptPrinterSettings.Default with { StoreName = "  s001  " };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, settings, receipt.SoldAt);

        Assert.Contains(document.Elements, element => element.Text == "Store: S001");
        Assert.DoesNotContain(document.Elements, element => element.Text.Contains("S001 (S001)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Receipt_text_formatter_uses_safe_store_fallback_when_name_and_code_are_blank()
    {
        var receipt = CreateReceipt(Guid.NewGuid()) with { StoreCode = "   " };
        var settings = ReceiptPrinterSettings.Default with { StoreName = "   " };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, settings, receipt.SoldAt);

        Assert.Contains(document.Elements, element => element.Text == "Store: -");
    }

    [Fact]
    public void Receipt_text_formatter_wraps_every_store_line_to_receipt_width()
    {
        var receipt = CreateReceipt(Guid.NewGuid());
        var settings = ReceiptPrinterSettings.Default with
        {
            StoreName = "The Very Long Sunnybank Shopping Centre Main Store"
        };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, settings, receipt.SoldAt);

        var storeLines = document.Elements
            .SkipWhile(element => element.Text != "Cashier: Alice")
            .Skip(1)
            .TakeWhile(element => element.Text != "Device: POS-01")
            .Select(element => element.Text)
            .ToList();
        Assert.NotEmpty(storeLines);
        Assert.StartsWith("Store: ", storeLines[0], StringComparison.Ordinal);
        Assert.Equal(
            "Store: The Very Long Sunnybank Shopping Centre Main Store (S001)",
            string.Join(" ", storeLines));
        Assert.All(storeLines, line => Assert.True(line.Length <= 42, $"Store line exceeds 42 characters: {line}"));
    }

    [Fact]
    public void Receipt_text_formatter_does_not_print_success_page_cash_change_preview_rows()
    {
        var receipt = CreateReceipt(Guid.NewGuid(), tenderedAmount: 10m, changeAmount: 1m);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.DoesNotContain(document.PreviewRows, row => row.Text.Contains("Tendered", StringComparison.Ordinal));
        Assert.DoesNotContain(document.PreviewRows, row => row.Text.Contains("Change", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Elements, element => element.Text.Contains("Tendered", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Elements, element => element.Text.Contains("Change", StringComparison.Ordinal));
    }

    [Fact]
    public void Receipt_text_formatter_prints_emergency_override_username_without_password_label()
    {
        var session = CashierSessionContext.CreateEmergencyOverride(
            "S001", "POS-01", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), "token");
        var receipt = CreateReceipt(Guid.NewGuid(), cashierName: session.CashierName);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.Contains("Cashier: EMERGENCY", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("\u8d85\u7ea7\u5bc6\u7801", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_text_formatter_does_not_append_voucher_balance_to_complete_receipt()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            paymentReference: "VOUCHER:VC200:LOCK-1:12.34",
            paymentMethod: PaymentMethodKind.Voucher);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.DoesNotContain("VOUCHER BALANCE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == "VC200");
        Assert.DoesNotContain(document.Elements, element => element.Kind == ReceiptPrintElementKind.QrCode && element.Text == "VC200");
        Assert.DoesNotContain(document.PreviewRows, row => row.Text.Contains("VOUCHER BALANCE", StringComparison.Ordinal));
    }

    [Fact]
    public void Receipt_text_formatter_prints_voucher_balance_as_standalone_document()
    {
        var orderGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var printTime = new DateTimeOffset(2026, 7, 10, 9, 30, 0, TimeSpan.Zero);
        var receipt = CreateReceipt(orderGuid) with
        {
            VoucherBalance = new VoucherBalanceReceipt(" VC200 ", 12.34m)
        };
        var settings = ReceiptPrinterSettings.Default with { StoreName = "Sunnybank" };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, settings, printTime);

        Assert.Contains("VOUCHER BALANCE", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Voucher: VC200", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Balance: $12.34", document.PlainText, StringComparison.Ordinal);
        Assert.Contains(document.Elements, element => element.Text == "Order:");
        Assert.Contains(document.Elements, element => element.Text == orderGuid.ToString());
        Assert.Contains($"Print Time: {printTime.ToLocalTime():yyyy-MM-dd HH:mm:ss}", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Store: Sunnybank (S001)", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Organic Gala Apples", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Payment:", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("APPROVED CARD RECEIPT", document.PlainText, StringComparison.Ordinal);
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == "VC200");
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.QrCode && element.Text == "VC200");
        Assert.Contains(document.PreviewRows, row => row.Text == "Voucher: VC200");
        Assert.Equal("VC200", Assert.Single(document.PreviewRows, row => row.IsQrCode).QrCodeValue);
    }

    [Fact]
    public void Receipt_text_formatter_wraps_long_voucher_balance_text_to_receipt_width()
    {
        var receipt = CreateReceipt(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")) with
        {
            OrderDisplay = "ORDER-20260710-VERY-LONG-REFERENCE-1234567890",
            VoucherBalance = new VoucherBalanceReceipt(
                "VC200-VERY-LONG-VOUCHER-CODE-ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                12.34m)
        };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.All(
            document.Elements.Where(element => element.Kind == ReceiptPrintElementKind.Text),
            element => Assert.True(element.Text.Length <= 42, $"Receipt line exceeds 42 characters: {element.Text}"));
        Assert.Contains(document.Elements, element => element.Text == "Voucher:");
        Assert.Contains(document.Elements, element => element.Text == "Order:");
    }

    [Fact]
    public void Receipt_text_formatter_prints_refund_voucher_as_standalone_voucher_document()
    {
        var orderGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var printTime = new DateTimeOffset(2026, 7, 10, 9, 30, 0, TimeSpan.Zero);
        var receipt = CreateReceipt(orderGuid, paymentReference: "VOUCHER_REFUND:RF123", paymentMethod: PaymentMethodKind.Voucher) with
        {
            TotalAmount = -8m,
            ActualAmount = -8m,
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF123")],
            RefundVoucher = new RefundVoucherReceipt("RF123", 8m)
        };
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, printTime);

        Assert.Contains("REFUND VOUCHER", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Voucher: RF123", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("Amount: $8.00", document.PlainText, StringComparison.Ordinal);
        Assert.Contains($"Order: {orderGuid}", document.PlainText, StringComparison.Ordinal);
        Assert.Contains($"Print Time: {printTime.ToLocalTime():yyyy-MM-dd HH:mm:ss}", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Organic Gala Apples", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Payment:", document.PlainText, StringComparison.Ordinal);
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == "RF123");
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.QrCode && element.Text == "RF123");
        Assert.Equal("RF123", Assert.Single(document.PreviewRows, row => row.IsQrCode).QrCodeValue);
    }

    [Fact]
    public async Task Receipt_print_service_uses_actual_print_time_for_voucher_refund_auto()
    {
        var receipt = CreateReceipt(Guid.NewGuid()) with
        {
            SoldAt = new DateTimeOffset(2020, 1, 1, 9, 30, 0, TimeSpan.Zero),
            TotalAmount = -8m,
            ActualAmount = -8m,
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF123")],
            RefundVoucher = new RefundVoucherReceipt("RF123", 8m)
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver);
        var before = DateTime.Now.AddSeconds(-1);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.VoucherRefundAuto);

        var after = DateTime.Now.AddSeconds(1);
        Assert.True(result.Succeeded);
        var printTimeText = Assert.Single(driver.LastDocument!.Elements, element =>
            element.Kind == ReceiptPrintElementKind.Text && element.Text.StartsWith("Print Time: ", StringComparison.Ordinal)).Text;
        var printedAt = DateTime.ParseExact(
            printTimeText["Print Time: ".Length..],
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        Assert.InRange(printedAt, before, after);
    }

    [Fact]
    public async Task Receipt_print_service_uses_actual_time_and_skips_card_marker_for_voucher_balance_auto()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            paymentReference: "ANZBACKEND:260601120001:session=11111111-2222-3333-4444-555555555555:environment=Sandbox") with
        {
            SoldAt = new DateTimeOffset(2020, 1, 1, 9, 30, 0, TimeSpan.Zero),
            VoucherBalance = new VoucherBalanceReceipt("VC200", 12.34m)
        };
        var driver = new RecordingReceiptPrinterDriver();
        var notifier = new RecordingCardReceiptPrintedNotifier();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver,
            [notifier]);
        var before = DateTime.Now.AddSeconds(-1);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.VoucherBalanceAuto);

        var after = DateTime.Now.AddSeconds(1);
        Assert.True(result.Succeeded);
        Assert.Empty(notifier.Calls);
        var printTimeText = Assert.Single(driver.LastDocument!.Elements, element =>
            element.Kind == ReceiptPrintElementKind.Text && element.Text.StartsWith("Print Time: ", StringComparison.Ordinal)).Text;
        var printedAt = DateTime.ParseExact(
            printTimeText["Print Time: ".Length..],
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        Assert.InRange(printedAt, before, after);
    }

    [Fact]
    public void Refund_voucher_document_prints_expiry_under_amount_and_usage_terms_below_codes()
    {
        var expiry = new DateTimeOffset(2027, 1, 5, 23, 59, 59, TimeSpan.FromHours(11));
        var receipt = CreateReceipt(Guid.NewGuid(), paymentReference: "VOUCHER_REFUND:RF123", paymentMethod: PaymentMethodKind.Voucher) with
        {
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF123")],
            RefundVoucher = new RefundVoucherReceipt("RF123", 8m, expiry)
        };

        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        var elements = document.Elements;
        var amountAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "Amount: $8.00");
        var expiryAt = IndexOfElement(elements, element =>
            element.Kind == ReceiptPrintElementKind.Text && element.Text == $"Valid until: {expiry.ToLocalTime():yyyy-MM-dd}");
        var qrAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.QrCode);
        var termsAt = IndexOfElement(elements, element => element.Kind == ReceiptPrintElementKind.Text && element.Text == "VOUCHER TERMS");
        // 到期日紧跟金额行；使用说明在条码和二维码之后，位置用元素序列判断（PlainText 不含条码文本）。
        Assert.True(amountAt >= 0);
        Assert.Equal(amountAt + 1, expiryAt);
        Assert.True(qrAt >= 0 && qrAt < termsAt, "使用说明必须在条码/二维码之后");
        Assert.True(elements[expiryAt].IsEmphasized);

        // 条款句子较长会按纸宽自动换行，所以按折叠空白后的整句断言。
        var collapsed = System.Text.RegularExpressions.Regex.Replace(document.PlainText, @"\s+", " ");
        Assert.Contains("Use at the issuing store only.", collapsed, StringComparison.Ordinal);
        Assert.Contains("Pay with it at checkout by scanning the barcode or QR code.", collapsed, StringComparison.Ordinal);
        Assert.Contains("Can be used across several purchases until the balance is $0.00.", collapsed, StringComparison.Ordinal);
        Assert.Contains("Not redeemable for cash.", collapsed, StringComparison.Ordinal);
        // 使用说明块里每一行都不超过 42 字符纸宽。
        Assert.All(
            elements.Skip(termsAt).Where(element => element.Kind == ReceiptPrintElementKind.Text),
            element => Assert.True(element.Text.Length <= 42, element.Text));
    }

    [Fact]
    public void Refund_voucher_document_without_known_expiry_omits_valid_until_but_keeps_usage_terms()
    {
        var receipt = CreateReceipt(Guid.NewGuid(), paymentReference: "VOUCHER_REFUND:RF124", paymentMethod: PaymentMethodKind.Voucher) with
        {
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF124")],
            RefundVoucher = new RefundVoucherReceipt("RF124", 8m)
        };

        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        // 旧券无到期、离线或查不到时不印任何猜测日期，但券面仍要能出票并带使用说明。
        Assert.DoesNotContain("Valid until", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("VOUCHER TERMS", document.PlainText, StringComparison.Ordinal);
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == "RF124");
    }

    [Fact]
    public void Voucher_balance_document_prints_expiry_but_not_refund_voucher_terms()
    {
        var expiry = new DateTimeOffset(2027, 1, 5, 23, 59, 59, TimeSpan.FromHours(11));
        var receipt = CreateReceipt(Guid.NewGuid()) with
        {
            VoucherBalance = new VoucherBalanceReceipt("VC200", 12.34m, expiry)
        };

        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.Contains($"Valid until: {expiry.ToLocalTime():yyyy-MM-dd}", document.PlainText, StringComparison.Ordinal);
        // 余额凭证对应的券类型不限于退款券，不一定限本店，不套用退款券的使用说明。
        Assert.DoesNotContain("VOUCHER TERMS", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("issuing store", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_print_service_fills_refund_voucher_expiry_from_lookup_before_printing()
    {
        var expiry = new DateTimeOffset(2027, 1, 5, 23, 59, 59, TimeSpan.FromHours(11));
        var lookup = new FakeVoucherExpiryLookup(expiry);
        var receipt = CreateReceipt(Guid.NewGuid()) with
        {
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF123")],
            RefundVoucher = new RefundVoucherReceipt("RF123", 8m)
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver,
            voucherExpiryLookup: lookup);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.VoucherRefundAuto);

        Assert.True(result.Succeeded);
        var call = Assert.Single(lookup.Calls);
        Assert.Equal((receipt.StoreCode, "RF123"), call);
        Assert.Contains(
            driver.LastDocument!.Elements,
            element => element.Text == $"Valid until: {expiry.ToLocalTime():yyyy-MM-dd}");
    }

    [Fact]
    public async Task Receipt_print_service_still_prints_voucher_without_expiry_when_lookup_finds_nothing()
    {
        var lookup = new FakeVoucherExpiryLookup(null);
        var receipt = CreateReceipt(Guid.NewGuid()) with
        {
            Payments = [new ReceiptPaymentLine(PaymentMethodKind.Voucher, -8m, "VOUCHER_REFUND:RF123")],
            RefundVoucher = new RefundVoucherReceipt("RF123", 8m)
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver,
            voucherExpiryLookup: lookup);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.VoucherRefundAuto);

        // 离线/查不到只是少印日期，绝不能阻断出票。
        Assert.True(result.Succeeded);
        Assert.Single(lookup.Calls);
        Assert.DoesNotContain(driver.LastDocument!.Elements, element => element.Text.StartsWith("Valid until", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == "RF123");
    }

    [Fact]
    public async Task Receipt_print_service_does_not_query_expiry_for_regular_receipts_or_when_expiry_is_known()
    {
        var lookup = new FakeVoucherExpiryLookup(new DateTimeOffset(2027, 1, 5, 23, 59, 59, TimeSpan.FromHours(11)));
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver,
            voucherExpiryLookup: lookup);
        var knownExpiry = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(11));

        await service.PrintReceiptAsync(CreateReceipt(Guid.NewGuid()), ReceiptPrintReason.Manual);
        await service.PrintReceiptAsync(
            CreateReceipt(Guid.NewGuid()) with { RefundVoucher = new RefundVoucherReceipt("RF900", 8m, knownExpiry) },
            ReceiptPrintReason.VoucherRefundAuto);

        // 普通小票不查；券面已经带着到期日（例如调用方已知）也不重复查询。
        Assert.Empty(lookup.Calls);
        Assert.Contains(
            driver.LastDocument!.Elements,
            element => element.Text == $"Valid until: {knownExpiry.ToLocalTime():yyyy-MM-dd}");
    }

    [Fact]
    public async Task Receipt_print_service_fills_voucher_balance_expiry_from_lookup()
    {
        var expiry = new DateTimeOffset(2027, 3, 2, 23, 59, 59, TimeSpan.FromHours(10));
        var lookup = new FakeVoucherExpiryLookup(expiry);
        var receipt = CreateReceipt(Guid.NewGuid()) with
        {
            VoucherBalance = new VoucherBalanceReceipt("VC200", 12.34m)
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver,
            voucherExpiryLookup: lookup);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.VoucherBalanceAuto);

        Assert.True(result.Succeeded);
        Assert.Equal((receipt.StoreCode, "VC200"), Assert.Single(lookup.Calls));
        Assert.Contains(
            driver.LastDocument!.Elements,
            element => element.Text == $"Valid until: {expiry.ToLocalTime():yyyy-MM-dd}");
    }

    private sealed class FakeVoucherExpiryLookup(DateTimeOffset? expiry) : IVoucherExpiryLookup
    {
        public List<(string StoreCode, string VoucherCode)> Calls { get; } = [];

        public Task<DateTimeOffset?> FindExpiryAsync(string storeCode, string voucherCode, CancellationToken cancellationToken)
        {
            Calls.Add((storeCode, voucherCode));
            return Task.FromResult(expiry);
        }
    }

    [Fact]
    public void Receipt_text_formatter_skips_remaining_voucher_section_without_positive_balance()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            paymentReference: "VOUCHER:VC201:LOCK-1:0.00",
            paymentMethod: PaymentMethodKind.Voucher);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.DoesNotContain("VOUCHER BALANCE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == "VC201");
        Assert.DoesNotContain(document.Elements, element => element.Kind == ReceiptPrintElementKind.QrCode && element.Text == "VC201");
    }

    [Fact]
    public void Receipt_text_formatter_masks_full_pan_in_embedded_bank_receipt_text()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            bankReceiptText:
                "APPROVED CARD RECEIPT\n" +
                "CARD 4111111111111234\n" +
                "ALT 4111 1111 1111 5678\n" +
                "DASH 4111-1111-1111-9999\n" +
                "TAB 4111\t1111\t1111\t2468\n" +
                "NBSP 4111\u00A01111\u00A01111\u00A01357\n" +
                "MULTI 4111  1111  1111  8642\n" +
                "DOT 4111.1111.1111.9753");
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.Contains("CARD ****1234", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("ALT ****5678", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("DASH ****9999", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("TAB ****2468", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("NBSP ****1357", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("MULTI ****8642", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("DOT ****9753", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111234", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111 1111 1111 5678", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111-1111-1111-9999", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111\t1111\t1111\t2468", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111\u00A01111\u00A01111\u00A01357", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111  1111  1111  8642", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111.1111.1111.9753", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_text_formatter_keeps_bank_reference_numbers_while_masking_pan()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            bankReceiptText:
                "TXN REF 260601120038\n" +
                "RRN 123456789012\n" +
                "CARD 4111111111111234");
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.Contains("TXN REF 260601120038", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("RRN 123456789012", document.PlainText, StringComparison.Ordinal);
        Assert.Contains("CARD ****1234", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111234", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_text_formatter_omits_embedded_bank_receipt_text_when_disabled_but_keeps_card_summary()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            bankReceiptText:
                "APPROVED CARD RECEIPT\n" +
                "TXN REF 260601120038\n" +
                "CUSTOMER COPY");
        var formatter = new ReceiptTextFormatter();

        var enabled = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);
        var disabled = formatter.Build(
            receipt,
            ReceiptPrinterSettings.Default with { PrintBankReceiptText = false },
            receipt.SoldAt);

        // 默认仍打印银行原文，保持旧行为。
        Assert.True(ReceiptPrinterSettings.Default.PrintBankReceiptText);
        Assert.Contains("CUSTOMER COPY", enabled.PlainText, StringComparison.Ordinal);
        // 关闭后整段银行原文不进入打印与预览，但付款行的卡类型/后四位仍保留。
        Assert.DoesNotContain("APPROVED CARD RECEIPT", disabled.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("TXN REF 260601120038", disabled.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("CUSTOMER COPY", disabled.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain(disabled.PreviewRows, row => row.Text.Contains("CUSTOMER COPY", StringComparison.Ordinal));
        Assert.Contains(disabled.PreviewRows, row =>
            row.Text.Contains("VISA", StringComparison.Ordinal) &&
            row.Text.Contains("****1111", StringComparison.Ordinal));
        Assert.Contains("Total(inc GST)", disabled.PlainText, StringComparison.Ordinal);
        Assert.True(disabled.Elements.Count < enabled.Elements.Count);
    }

    [Fact]
    public async Task Receipt_print_service_prints_latest_receipt_with_configured_settings()
    {
        var receipt = CreateReceipt(Guid.NewGuid());
        var query = new FakeReceiptQueryService { LatestReceipt = receipt };
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                PrinterPort = "COM3",
                BrandName = "HotBargain",
                StoreName = "Main Store"
            }
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(query, settingsStore, new ReceiptTextFormatter(), driver);

        var result = await service.PrintLatestReceiptAsync(ReceiptPrintReason.LastReceipt);

        Assert.True(result.Succeeded);
        Assert.Equal(receipt.OrderGuid, result.OrderGuid);
        Assert.NotNull(driver.LastDocument);
        Assert.Equal("COM3", driver.LastSettings?.PrinterPort);
        Assert.Contains(driver.LastDocument!.PreviewRows, row => row.Text == "Main Store");
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text == $"Print Time: {receipt.SoldAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    [Fact]
    public async Task Receipt_print_service_returns_failure_when_latest_receipt_is_missing()
    {
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver);

        var result = await service.PrintLatestReceiptAsync(ReceiptPrintReason.LastReceipt);

        Assert.False(result.Succeeded);
        Assert.Null(result.OrderGuid);
        Assert.Null(driver.LastDocument);
    }

    [Fact]
    public async Task Receipt_print_service_returns_failure_when_driver_fails()
    {
        var receipt = CreateReceipt(Guid.NewGuid());
        var query = new FakeReceiptQueryService { LatestReceipt = receipt };
        var notifier = new RecordingCardReceiptPrintedNotifier();
        var driver = new RecordingReceiptPrinterDriver
        {
            PrintResult = new ReceiptPrinterDriverResult(false, "paper out")
        };
        var service = new ReceiptPrintService(
            query,
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver,
            [notifier]);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.Manual);

        Assert.False(result.Succeeded);
        Assert.Equal("paper out", result.Message);
        Assert.Equal(receipt.OrderGuid, result.OrderGuid);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Receipt_print_service_marks_linkly_backend_receipt_printed_after_driver_success()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            paymentReference: "ANZBACKEND:260601120001:session=11111111-2222-3333-4444-555555555555:environment=Sandbox");
        var notifier = new RecordingCardReceiptPrintedNotifier();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            new RecordingReceiptPrinterDriver(),
            [notifier]);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.CardAuto);

        Assert.True(result.Succeeded);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal("Sandbox", call.Environment);
        Assert.Equal("11111111-2222-3333-4444-555555555555", call.SessionId);
    }

    [Fact]
    public async Task Receipt_print_service_keeps_success_when_printed_marker_times_out()
    {
        var receipt = CreateReceipt(
            Guid.NewGuid(),
            paymentReference: "ANZBACKEND:260601120002:session=22222222-2222-4222-8222-222222222222:environment=Sandbox");
        var notifier = new RecordingCardReceiptPrintedNotifier
        {
            Exception = new TaskCanceledException("backend timeout")
        };
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            new RecordingReceiptPrinterDriver(),
            [notifier]);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.CardAuto);

        Assert.True(result.Succeeded);
        Assert.Equal(receipt.OrderGuid, result.OrderGuid);
    }

    [Fact]
    public async Task Receipt_print_service_serializes_concurrent_prints()
    {
        var first = CreateReceipt(Guid.NewGuid());
        var second = CreateReceipt(Guid.NewGuid());
        var query = new FakeReceiptQueryService();
        query.Receipts[first.OrderGuid] = first;
        query.Receipts[second.OrderGuid] = second;
        var driver = new DelayedReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            query,
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver);

        await Task.WhenAll(
            service.PrintReceiptAsync(first.OrderGuid, ReceiptPrintReason.Manual),
            service.PrintReceiptAsync(second.OrderGuid, ReceiptPrintReason.Reprint));

        Assert.Equal(2, driver.PrintCount);
        Assert.Equal(1, driver.MaxConcurrentPrints);
    }

    [Fact]
    public async Task Cash_drawer_service_opens_with_configured_printer_settings()
    {
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with { PrinterPort = "COM5" }
        };
        var driver = new RecordingReceiptPrinterDriver
        {
            OpenCashDrawerResult = new ReceiptPrinterDriverResult(true, "Cash drawer opened.")
        };
        var service = new CashDrawerService(settingsStore, driver);

        var result = await service.OpenAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("Cash drawer opened.", result.Message);
        Assert.Equal("COM5", driver.LastCashDrawerSettings?.PrinterPort);
        Assert.Equal(1, driver.OpenCashDrawerCallCount);
    }

    [Fact]
    public async Task Cash_drawer_service_returns_failure_when_driver_fails()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            OpenCashDrawerResult = new ReceiptPrinterDriverResult(false, "drawer offline")
        };
        var service = new CashDrawerService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.OpenAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("drawer offline", result.Message);
    }

    [Fact]
    public async Task Cash_drawer_service_returns_failure_when_driver_throws()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            OpenCashDrawerException = new InvalidOperationException("sdk missing")
        };
        var service = new CashDrawerService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.OpenAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("sdk missing", result.Message);
    }

    [Fact]
    public async Task Receipt_printer_settings_store_persists_fields_and_defaults_port()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository);
        var settings = ReceiptPrinterSettings.Default with
        {
            PrinterPort = "   ",
            BrandName = "HB",
            StoreName = "Sunnybank",
            StoreAddress = "Shop 1",
            StorePhone = "07",
            Abn = "ABN",
            ReturnPolicy = "Return within 7 days",
            CutDistance = 80
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(1, repository.BatchWriteCount);
        Assert.Equal("USB,", loaded.PrinterPort);
        Assert.Equal("HB", loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
        Assert.Equal("Shop 1", loaded.StoreAddress);
        Assert.Equal("07", loaded.StorePhone);
        Assert.Equal("ABN", loaded.Abn);
        Assert.Equal("Return within 7 days", loaded.ReturnPolicy);
        Assert.Equal(80, loaded.CutDistance);
    }

    private static ReceiptDetails CreateReceipt(
        Guid orderGuid,
        decimal? tenderedAmount = null,
        decimal? changeAmount = null,
        string paymentReference = "ANZ:123",
        string bankReceiptText = "APPROVED CARD RECEIPT",
        PaymentMethodKind paymentMethod = PaymentMethodKind.Card,
        string cashierName = "Alice")
    {
        var cardTransactions = paymentMethod == PaymentMethodKind.Card
            ? new[]
            {
                new CardTransactionDto(
                    "Linkly",
                    "TXN-1",
                    "AUTH1",
                    "VISA",
                    411111,
                    "****1111",
                    "M1",
                    "00",
                    "APPROVED",
                    "123456",
                    new DateTimeOffset(2026, 5, 27, 9, 1, 0, TimeSpan.Zero),
                    9.00m,
                    bankReceiptText)
            }
            : null;

        return new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            cashierName,
            new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero),
            9.20m,
            0.20m,
            9.00m,
            [
                new ReceiptPreviewLine("Organic Gala Apples", "690101", 2m, 2.50m, 0m, 5.00m),
                new ReceiptPreviewLine("Whole Grain Bread", "690102", 1m, 4.20m, 0.20m, 4.00m)
            ],
            [
                new ReceiptPaymentLine(
                    paymentMethod,
                    9.00m,
                    paymentReference,
                    cardTransactions)
            ],
            tenderedAmount,
            changeAmount);
    }

    private static LocalInstallmentOrder CreateInstallmentOrder(
        InstallmentStatus status,
        decimal paidAmount,
        decimal balanceAmount,
        InstallmentPickupInfoDto? pickupInfo = null)
    {
        var createdAt = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero);
        return new LocalInstallmentOrder(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            "IO-20260704-0002",
            "S001",
            "POS-01",
            "user-1",
            "Alice",
            "Bob Buyer",
            "0400111222",
            createdAt,
            createdAt,
            80m,
            20m,
            20m,
            paidAmount,
            balanceAmount,
            status,
            [
                new InstallmentLineDto(
                    Guid.NewGuid(),
                    "SKU-INST",
                    null,
                    "Installment Tea",
                    "939003",
                    1m,
                    80m,
                    0m,
                    80m)
            ],
            [
                new InstallmentPaymentDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Cash,
                    paidAmount,
                    "CASH",
                    InstallmentPaymentStatus.Recorded,
                    createdAt,
                    "user-1",
                    "POS-01")
            ],
            pickupInfo);
    }

    private sealed class RecordingCardReceiptPrintedNotifier : ICardReceiptPrintedNotifier
    {
        public List<(string Environment, string SessionId)> Calls { get; } = [];

        public Exception? Exception { get; init; }

        public Task MarkReceiptPrintedAsync(
            string environment,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((environment, sessionId));
            if (Exception is not null)
            {
                return Task.FromException(Exception);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptQueryService : IReceiptQueryService
    {
        public ReceiptDetails? LatestReceipt { get; init; }

        public Dictionary<Guid, ReceiptDetails> Receipts { get; } = [];

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            if (Receipts.TryGetValue(orderGuid, out var receipt))
            {
                return Task.FromResult<ReceiptDetails?>(receipt);
            }

            return Task.FromResult(LatestReceipt?.OrderGuid == orderGuid ? LatestReceipt : null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LatestReceipt);
        }
    }

    private sealed class FakeReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; init; } = ReceiptPrinterSettings.Default;

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReceiptPrinterDriver : IReceiptPrinterDriver
    {
        public ReceiptPrintDocument? LastDocument { get; private set; }

        public ReceiptPrinterSettings? LastSettings { get; private set; }

        public ReceiptPrinterSettings? LastCashDrawerSettings { get; private set; }

        public ReceiptPrinterDriverResult PrintResult { get; init; } = new(true, "printed");

        public ReceiptPrinterDriverResult OpenCashDrawerResult { get; init; } = new(true, "drawer opened");

        public Exception? OpenCashDrawerException { get; init; }

        public int OpenCashDrawerCallCount { get; private set; }

        public Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastDocument = document;
            LastSettings = settings;
            return Task.FromResult(PrintResult);
        }

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "tested"));
        }

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            OpenCashDrawerCallCount++;
            LastCashDrawerSettings = settings;
            if (OpenCashDrawerException is not null)
            {
                throw OpenCashDrawerException;
            }

            return Task.FromResult(OpenCashDrawerResult);
        }
    }

    private sealed class DelayedReceiptPrinterDriver : IReceiptPrinterDriver
    {
        private readonly object _gate = new();
        private int _activePrints;

        public int PrintCount { get; private set; }

        public int MaxConcurrentPrints { get; private set; }

        public async Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activePrints);
            lock (_gate)
            {
                PrintCount++;
                MaxConcurrentPrints = Math.Max(MaxConcurrentPrints, active);
            }

            try
            {
                await Task.Delay(40, cancellationToken);
                return new ReceiptPrinterDriverResult(true, "printed");
            }
            finally
            {
                Interlocked.Decrement(ref _activePrints);
            }
        }

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "tested"));
        }

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "drawer opened"));
        }
    }

    private sealed class InMemorySettingsRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public int BatchWriteCount { get; private set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            BatchWriteCount++;
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
