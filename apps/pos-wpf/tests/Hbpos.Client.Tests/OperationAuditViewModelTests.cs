using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[Collection(GlobalLoggingTestCollection.Name)]
public sealed class OperationAuditViewModelTests
{
    [Fact]
    public async Task Pos_terminal_scan_records_single_cart_add_without_promotion_duplicate()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        var item = CreateItem("SKU-SCAN", "Scan Tea", "9300000", 5m);
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            promotionEvaluationService: new SingleLinePromotionEvaluationService(1m),
            operationAuditLogger: logger)
        {
            ScanText = item.LookupCode
        };

        await viewModel.ScanCommand.ExecuteAsync(null);
        for (var attempt = 0; attempt < 100 && cart.DiscountAmount != 1m; attempt++)
        {
            await Task.Delay(5);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("CART_ITEM_ADD", auditEvent.OperationType);
        Assert.Equal("SCAN", auditEvent.ReasonCode);
        Assert.NotNull(auditEvent.TraceId);
        Assert.Equal(1m, cart.DiscountAmount);
    }

    [Fact]
    public void Pos_terminal_denied_high_risk_cart_action_records_denied_without_state_change()
    {
        var logger = new RecordingOperationAuditLogger();
        var item = CreateItem("SKU-DENIED", "Denied Tea", "9300099", 5m);
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            enforcePermissionsWhenNoCashier: true,
            operationAuditLogger: logger)
        {
            SelectedItem = item
        };

        viewModel.AddSelectedCommand.Execute(null);

        Assert.Empty(viewModel.CartLines);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("CART_ITEM_ADD", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("PERMISSION_DENIED", auditEvent.ReasonCode);
    }

    [Fact]
    public void Audit_logger_failure_does_not_rollback_completed_cart_change()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-LOGGER-FAIL", "Logger Failure Tea", "9300098", 7m);
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null,
            operationAuditLogger: new ThrowingOperationAuditLogger())
        {
            SelectedItem = item
        };

        viewModel.AddSelectedCommand.Execute(null);

        Assert.Equal(7m, cart.ActualAmount);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Pos_terminal_records_cart_mutations_with_before_after_amounts()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        var item = CreateItem("SKU-AUDIT", "Audit Tea", "9300001", 10m);
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            operationAuditLogger: logger)
        {
            SelectedItem = item
        };

        viewModel.AddSelectedCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.SelectedCartLine = line;
        viewModel.KeypadBuffer = "2";
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);
        viewModel.KeypadBuffer = "12";
        viewModel.ModifySelectedLinePriceCommand.Execute(null);
        viewModel.KeypadBuffer = "1";
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);
        viewModel.IsWholeOrderOperation = true;
        viewModel.ApplyQuickDiscountPercentCommand.Execute("10");
        viewModel.RemoveLineCommand.Execute(line);

        viewModel.SelectedItem = item;
        viewModel.AddSelectedCommand.Execute(null);
        viewModel.ClearCartCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(
            [
                "CART_ITEM_ADD",
                "CART_ITEM_QUANTITY_CHANGE",
                "CART_ITEM_PRICE_CHANGE",
                "CART_LINE_DISCOUNT_CHANGE",
                "CART_ORDER_DISCOUNT_CHANGE",
                "CART_ITEM_REMOVE",
                "CART_ITEM_ADD",
                "CART_CLEAR"
            ],
            logger.Events.Select(auditEvent => auditEvent.OperationType));
        Assert.All(logger.Events, auditEvent => Assert.Equal("Succeeded", auditEvent.Outcome));

        var add = logger.Events[0];
        Assert.Equal(0m, add.BeforeActual);
        Assert.Equal(10m, add.AfterActual);
        Assert.Equal(10m, add.AmountDelta);
        var addedItem = Assert.Single(add.Items);
        Assert.Equal("SKU-AUDIT", addedItem.ProductCode);
        Assert.Equal(0m, addedItem.BeforeQuantity);
        Assert.Equal(1m, addedItem.AfterQuantity);
        Assert.Equal(1m, addedItem.QuantityDelta);

        var removed = Assert.Single(logger.Events, auditEvent => auditEvent.OperationType == "CART_ITEM_REMOVE");
        Assert.Equal(0m, removed.AfterActual);
        Assert.True(removed.AmountDelta < 0m);
        Assert.Equal(0m, Assert.Single(removed.Items).AfterQuantity);

        var cleared = Assert.Single(logger.Events, auditEvent => auditEvent.OperationType == "CART_CLEAR");
        Assert.Equal(10m, cleared.BeforeActual);
        Assert.Equal(0m, cleared.AfterActual);
        Assert.Equal(-10m, cleared.AmountDelta);
    }

    [Fact]
    public async Task Pos_terminal_records_hold_reprint_and_manual_drawer_results_without_recall_navigation_noise()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        var item = CreateItem("SKU-ACTION", "Action Tea", "9300002", 6m);
        cart.AddItem(item);
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null,
            onHoldOrderAsync: () =>
            {
                cart.Clear();
                return Task.CompletedTask;
            },
            onRecallOrderAsync: () => Task.CompletedTask,
            onPrintLastReceiptAsync: () => Task.FromResult(new ReceiptPrintResult(false, "printer offline")),
            onOpenCashDrawerAsync: () => Task.FromResult(new ReceiptPrintResult(true, "opened")),
            operationAuditLogger: logger);

        await viewModel.HoldOrderCommand.ExecuteAsync(null);
        await viewModel.RecallOrderCommand.ExecuteAsync(null);
        await viewModel.PrintLastReceiptCommand.ExecuteAsync(null);
        await viewModel.OpenCashDrawerCommand.ExecuteAsync(null);

        Assert.Collection(
            logger.Events,
            auditEvent =>
            {
                Assert.Equal("ORDER_HOLD", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
                Assert.Equal(6m, auditEvent.BeforeActual);
                Assert.Equal(0m, auditEvent.AfterActual);
                Assert.Equal(-6m, auditEvent.AmountDelta);
            },
            auditEvent =>
            {
                Assert.Equal("RECEIPT_REPRINT", auditEvent.OperationType);
                Assert.Equal("Failed", auditEvent.Outcome);
                Assert.Equal("printer offline", auditEvent.SafeMessage);
            },
            auditEvent =>
            {
                Assert.Equal("CASH_DRAWER_OPEN", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
                Assert.Equal("MANUAL", auditEvent.ReasonCode);
            });
    }

    [Fact]
    public async Task Payment_records_tender_add_remove_and_sale_completion()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-PAY", "Payment Tea", "9300003", 10m));
        var workflow = new AuditPaymentWorkflow();
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        var firstTender = Assert.Single(viewModel.PaymentTenders);
        await viewModel.RemoveTenderCommand.ExecuteAsync(firstTender);
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Collection(
            logger.Events,
            auditEvent =>
            {
                Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType);
                Assert.Equal("Cash", auditEvent.PaymentMethod);
                Assert.Equal(10m, auditEvent.PaymentAmount);
            },
            auditEvent =>
            {
                Assert.Equal("PAYMENT_TENDER_REMOVE", auditEvent.OperationType);
                Assert.Equal("Cash", auditEvent.PaymentMethod);
                Assert.Equal(10m, auditEvent.PaymentAmount);
            },
            auditEvent => Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType),
            auditEvent =>
            {
                Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
                Assert.Equal("Cash", auditEvent.PaymentMethod);
                Assert.Equal(10m, auditEvent.PaymentAmount);
                Assert.NotNull(auditEvent.OrderGuid);
                Assert.Equal(10m, auditEvent.BeforeActual);
                Assert.Equal(0m, auditEvent.AfterActual);
                Assert.Equal(-10m, auditEvent.AmountDelta);
            });
    }

    [Fact]
    public async Task Payment_voucher_remove_release_failure_records_failed_without_removing_tender()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-VOUCHER-REMOVE", "Voucher Tea", "9300010", 10m));
        var workflow = new AuditPaymentWorkflow { ReleaseVoucherTenderResult = false };
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);
        var tender = new PaymentTender(
            PaymentMethodKind.Voucher,
            10m,
            "VOUCHER:SENSITIVE-CODE:SENSITIVE-TOKEN:0.00");
        viewModel.PaymentTenders.Add(tender);

        await viewModel.RemoveTenderCommand.ExecuteAsync(tender);

        Assert.Contains(tender, viewModel.PaymentTenders);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_REMOVE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("VOUCHER_RELEASE_FAILED", auditEvent.ReasonCode);
        Assert.Equal("Voucher", auditEvent.PaymentMethod);
        Assert.Equal(10m, auditEvent.PaymentAmount);
        Assert.DoesNotContain("SENSITIVE-CODE", auditEvent.SafeMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("SENSITIVE-TOKEN", auditEvent.SafeMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payment_manual_voucher_remove_release_exception_records_failed_and_shared_trace()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "MANUAL-RELEASE", 10m);
        var workflow = new AuditPaymentWorkflow
        {
            ReleaseVoucherTenderException = new TaskCanceledException("release timeout")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, operationAuditLogger: logger);
        var tender = new PaymentTender(PaymentMethodKind.Voucher, 10m, "VOUCHER:SECRET:SECRET");
        viewModel.PaymentTenders.Add(tender);

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => viewModel.RemoveTenderCommand.ExecuteAsync(tender));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        Assert.Contains(tender, viewModel.PaymentTenders);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_REMOVE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("VOUCHER_RELEASE_EXCEPTION", auditEvent.ReasonCode);
        Assert.Equal("Voucher", auditEvent.PaymentMethod);
        Assert.Equal(10m, auditEvent.PaymentAmount);
        Assert.Single(auditEvent.Items);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Theory]
    [InlineData(true, "Succeeded", "CARD_FAILURE_AUTO_RELEASE")]
    [InlineData(false, "Failed", "CARD_FAILURE_RELEASE_FAILED")]
    public async Task Payment_card_failure_auto_voucher_release_records_each_result(
        bool releaseSucceeded,
        string expectedOutcome,
        string expectedReasonCode)
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = CreatePaymentCart(isRefund: false, "AUTO-RELEASE", 20m);
        var workflow = new AuditPaymentWorkflow
        {
            ReleaseVoucherTenderResult = releaseSucceeded
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, operationAuditLogger: logger, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)))
        {
            TenderAmountText = "10",
            VoucherCodeText = "SAFE-VOUCHER"
        };
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        var voucher = Assert.Single(viewModel.PaymentTenders);
        logger.Events.Clear();
        workflow.FailTender = true;
        viewModel.TenderAmountText = "10";

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        var cardFailure = Assert.Single(
            logger.Events,
            auditEvent => auditEvent.OperationType == "PAYMENT_TENDER_ADD");
        Assert.Equal(10m, cardFailure.PaymentAmount);
        var releaseEvent = Assert.Single(
            logger.Events,
            auditEvent => auditEvent.OperationType == "PAYMENT_TENDER_REMOVE");
        Assert.Equal(expectedOutcome, releaseEvent.Outcome);
        Assert.Equal(expectedReasonCode, releaseEvent.ReasonCode);
        Assert.Equal("Voucher", releaseEvent.PaymentMethod);
        Assert.Equal(10m, releaseEvent.PaymentAmount);
        Assert.Single(releaseEvent.Items);
        Assert.Equal(releaseSucceeded, !viewModel.PaymentTenders.Contains(voucher));
    }

    [Fact]
    public async Task Payment_card_failure_auto_voucher_release_exception_records_failed_and_shared_trace()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "AUTO-RELEASE-ERROR", 20m);
        var workflow = new AuditPaymentWorkflow
        {
            ReleaseVoucherTenderException = new InvalidOperationException("release unavailable")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, operationAuditLogger: logger, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)))
        {
            TenderAmountText = "10",
            VoucherCodeText = "SAFE-VOUCHER"
        };
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        var voucher = Assert.Single(viewModel.PaymentTenders);
        logger.Events.Clear();
        workflow.FailTender = true;
        viewModel.TenderAmountText = "10";

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewModel.SelectCardCommand.ExecuteAsync(null));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        Assert.Contains(voucher, viewModel.PaymentTenders);
        var releaseEvent = Assert.Single(
            logger.Events,
            auditEvent => auditEvent.OperationType == "PAYMENT_TENDER_REMOVE");
        Assert.Equal("Failed", releaseEvent.Outcome);
        Assert.Equal("CARD_FAILURE_RELEASE_EXCEPTION", releaseEvent.ReasonCode);
        Assert.Equal("Voucher", releaseEvent.PaymentMethod);
        Assert.Equal(10m, releaseEvent.PaymentAmount);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(releaseEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Payment_installment_repayment_exception_records_failed_and_shared_system_trace()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var workflow = new AuditPaymentWorkflow();
        var order = CreateInstallmentOrder(outstandingAmount: 30m);
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            workflow,
            Session,
            installmentOrderService: new ThrowingInstallmentOrderService(),
            operationAuditLogger: logger);
        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.TenderAmountText = "30";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(
            logger.Events,
            auditEvent => auditEvent.OperationType == "INSTALLMENT_REPAYMENT_COMPLETE");
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("REPAYMENT", auditEvent.ReasonCode);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(30m, auditEvent.PaymentAmount);
        Assert.Equal(order.OrderId.ToString("D"), auditEvent.OrderGuid);
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.CorrelationId));
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "InstallmentAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Payment_installment_create_exception_records_sale_failed_and_shared_system_trace()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-INSTALLMENT-CREATE", "Installment Tea", "9300011", 60m));
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            installmentOrderService: new ThrowingInstallmentOrderService(),
            operationAuditLogger: logger)
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Customer",
            InstallmentCustomerPhone = "0400000000",
            TenderAmountText = "20"
        };
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(
            logger.Events,
            auditEvent => auditEvent.OperationType == "SALE_COMPLETE");
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("INSTALLMENT_CREATE", auditEvent.ReasonCode);
        Assert.Equal("Installment+Cash", auditEvent.PaymentMethod);
        Assert.Equal(20m, auditEvent.PaymentAmount);
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.CorrelationId));
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "InstallmentAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Payment_denied_tender_records_denied_without_adding_tender()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-PAY-DENIED", "Denied Payment Tea", "9300004", 10m));
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            enforcePermissionsWhenNoCashier: true,
            operationAuditLogger: logger);

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(10m, auditEvent.PaymentAmount);
    }

    [Fact]
    public async Task Payment_denied_voucher_remove_records_actual_method_and_amount()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = CreatePaymentCart(isRefund: false, "DENIED-REMOVE", 10m);
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            enforcePermissionsWhenNoCashier: true,
            operationAuditLogger: logger);
        var tender = new PaymentTender(PaymentMethodKind.Voucher, 10m, "VOUCHER:SECRET:SECRET");
        viewModel.PaymentTenders.Add(tender);

        await viewModel.RemoveTenderCommand.ExecuteAsync(tender);

        Assert.Contains(tender, viewModel.PaymentTenders);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_REMOVE", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("Voucher", auditEvent.PaymentMethod);
        Assert.Equal(10m, auditEvent.PaymentAmount);
    }

    [Fact]
    public async Task Payment_denied_regular_confirm_records_tenders_and_amount()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = CreatePaymentCart(isRefund: false, "DENIED-CONFIRM", 10m);
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            enforcePermissionsWhenNoCashier: true,
            operationAuditLogger: logger);
        viewModel.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 10m));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(10m, auditEvent.PaymentAmount);
    }

    [Fact]
    public async Task Payment_denied_installment_repayment_confirm_uses_repayment_event_and_order()
    {
        var logger = new RecordingOperationAuditLogger();
        var order = CreateInstallmentOrder(30m);
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            new AuditPaymentWorkflow(),
            Session,
            enforcePermissionsWhenNoCashier: true,
            installmentOrderService: new ThrowingInstallmentOrderService(),
            operationAuditLogger: logger);
        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 30m));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("INSTALLMENT_REPAYMENT_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(30m, auditEvent.PaymentAmount);
        Assert.Equal(order.OrderId.ToString("D"), auditEvent.OrderGuid);
    }

    [Fact]
    public async Task Payment_denied_new_installment_confirm_records_installment_method_and_deposit()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = CreatePaymentCart(isRefund: false, "DENIED-INSTALLMENT", 60m);
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            enforcePermissionsWhenNoCashier: true,
            installmentOrderService: new ThrowingInstallmentOrderService(),
            operationAuditLogger: logger)
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Customer",
            InstallmentCustomerPhone = "0400000000"
        };
        viewModel.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 20m));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.Equal("Installment+Cash", auditEvent.PaymentMethod);
        Assert.Equal(20m, auditEvent.PaymentAmount);
    }

    [Fact]
    public async Task Payment_failed_tender_records_safe_failure_without_adding_tender()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-PAY-FAILED", "Failed Payment Tea", "9300097", 10m));
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow { FailTender = true },
            Session,
            operationAuditLogger: logger);

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("payment.status.declined", auditEvent.ReasonCode);
        Assert.Equal("Tender declined", auditEvent.SafeMessage);
        Assert.Equal(10m, auditEvent.PaymentAmount);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash, false, 7)]
    [InlineData(PaymentMethodKind.Voucher, false, 7)]
    [InlineData(PaymentMethodKind.Cash, true, -7)]
    public async Task Payment_non_card_tender_exception_records_method_signed_amount_without_propagating(
        PaymentMethodKind method,
        bool isRefund,
        int expectedAmount)
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund, "TECHNICAL-TENDER", 7m);
        var workflow = new AuditPaymentWorkflow
        {
            AddTenderException = new InvalidOperationException("tender unavailable")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, operationAuditLogger: logger, paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)))
        {
            TenderAmountText = "7",
            VoucherCodeText = method == PaymentMethodKind.Voucher ? "SAFE-VOUCHER" : string.Empty
        };

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await (method == PaymentMethodKind.Cash
                ? viewModel.SelectCashCommand.ExecuteAsync(null)
                : viewModel.SelectVoucherCommand.ExecuteAsync(null));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("TENDER_EXCEPTION", auditEvent.ReasonCode);
        Assert.Equal(method.ToString(), auditEvent.PaymentMethod);
        Assert.Equal((decimal)expectedAmount, auditEvent.PaymentAmount);
        Assert.Single(auditEvent.Items);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
        Assert.Empty(viewModel.PaymentTenders);
    }

    [Fact]
    public async Task Payment_non_card_tender_task_canceled_records_failed_without_propagating()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "CANCELLED-TENDER", 8m);
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow { AddTenderException = new TaskCanceledException("timeout") },
            Session,
            operationAuditLogger: logger)
        {
            TenderAmountText = "8"
        };

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.SelectCashCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("TENDER_CANCELLED", auditEvent.ReasonCode);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(8m, auditEvent.PaymentAmount);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Theory]
    [InlineData(true, "CARD_TIMEOUT")]
    [InlineData(false, "CARD_EXCEPTION")]
    public async Task Payment_card_tender_non_manual_exception_records_amount_and_shared_trace(
        bool isCancellation,
        string expectedReasonCode)
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "CARD-TECHNICAL", 9m);
        Exception exception = isCancellation
            ? new TaskCanceledException("terminal timeout")
            : new InvalidOperationException("terminal unavailable");
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow { AddTenderException = exception },
            Session,
            operationAuditLogger: logger)
        {
            TenderAmountText = "9"
        };

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.SelectCardCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_TENDER_ADD", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal(expectedReasonCode, auditEvent.ReasonCode);
        Assert.Equal("Card", auditEvent.PaymentMethod);
        Assert.Equal(9m, auditEvent.PaymentAmount);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Refund_completion_preserves_signed_amounts_and_records_refund_event()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-RETURN",
            "REF-RETURN",
            "Returned Tea",
            "9300005",
            "ITEM-RETURN",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            "StoreRetailPrice",
            "return-source-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            operationAuditLogger: logger);

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        var refund = Assert.Single(logger.Events, auditEvent => auditEvent.OperationType == "RETURN_REFUND_COMPLETE");
        Assert.Equal("Succeeded", refund.Outcome);
        Assert.Equal(-10m, refund.PaymentAmount);
        Assert.Equal(-10m, refund.BeforeActual);
        Assert.Equal(0m, refund.AfterActual);
        Assert.Equal(10m, refund.AmountDelta);
        var item = Assert.Single(refund.Items);
        Assert.Equal(-1m, item.BeforeQuantity);
        Assert.Equal(0m, item.AfterQuantity);
        Assert.Equal(1m, item.QuantityDelta);
        Assert.Equal(-10m, item.BeforeActualAmount);
    }

    [Fact]
    public async Task Payment_upload_failure_keeps_sale_item_amount_snapshot_with_zero_deltas()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-UPLOAD-FAILED", "Upload Failed Tea", "9300012", 10m));
        var orderGuid = Guid.NewGuid();
        var workflow = new AuditPaymentWorkflow
        {
            CompletePaymentException = new PaymentUploadFailedException(orderGuid, 10m, 0m, "upload failed")
        };
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);

        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events, auditEvent => auditEvent.OperationType == "SALE_COMPLETE");
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("UPLOAD_FAILED", auditEvent.ReasonCode);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal(10m, auditEvent.BeforeGross);
        Assert.Equal(10m, auditEvent.AfterGross);
        Assert.Equal(10m, auditEvent.BeforeActual);
        Assert.Equal(10m, auditEvent.AfterActual);
        Assert.Equal(0m, auditEvent.AmountDelta);
        var item = Assert.Single(auditEvent.Items);
        Assert.Equal("SKU-UPLOAD-FAILED", item.ProductCode);
        Assert.Equal(1m, item.BeforeQuantity);
        Assert.Equal(1m, item.AfterQuantity);
        Assert.Equal(0m, item.QuantityDelta);
        Assert.Equal(10m, item.BeforeGrossAmount);
        Assert.Equal(10m, item.AfterGrossAmount);
        Assert.Equal(0m, item.GrossAmountDelta);
        Assert.Equal(10m, item.BeforeActualAmount);
        Assert.Equal(10m, item.AfterActualAmount);
        Assert.Equal(0m, item.ActualAmountDelta);
        Assert.False(cart.IsEmpty);
    }

    [Fact]
    public async Task Payment_technical_failure_keeps_signed_refund_snapshot_without_propagating()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-RETURN-FAILED",
            "REF-RETURN-FAILED",
            "Failed Return Tea",
            "9300013",
            "ITEM-RETURN-FAILED",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            "StoreRetailPrice",
            "return-source-failed",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var workflow = new AuditPaymentWorkflow
        {
            CompletePaymentException = new InvalidOperationException("checkout unavailable")
        };
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(
            logger.Events,
            auditEvent => auditEvent.OperationType == "RETURN_REFUND_COMPLETE");
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("TECHNICAL_FAILURE", auditEvent.ReasonCode);
        Assert.Equal(-10m, auditEvent.PaymentAmount);
        Assert.Equal(-10m, auditEvent.BeforeActual);
        Assert.Equal(-10m, auditEvent.AfterActual);
        Assert.Equal(0m, auditEvent.AmountDelta);
        var item = Assert.Single(auditEvent.Items);
        Assert.Equal("Return", item.LineKind);
        Assert.Equal(-1m, item.BeforeQuantity);
        Assert.Equal(-1m, item.AfterQuantity);
        Assert.Equal(0m, item.QuantityDelta);
        Assert.Equal(-10m, item.BeforeActualAmount);
        Assert.Equal(-10m, item.AfterActualAmount);
        Assert.Equal(0m, item.ActualAmountDelta);
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.CorrelationId));
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
        Assert.False(cart.IsEmpty);
    }

    [Theory]
    [InlineData(false, "SALE_COMPLETE", 10, 1)]
    [InlineData(true, "RETURN_REFUND_COMPLETE", -10, -1)]
    public async Task Payment_upload_retry_failure_records_current_items_and_shared_trace(
        bool isRefund,
        string expectedOperationType,
        int expectedActual,
        int expectedQuantity)
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = new PosCartService();
        var expectedActualAmount = (decimal)expectedActual;
        var expectedQuantityAmount = (decimal)expectedQuantity;
        if (isRefund)
        {
            cart.AddReturnLine(new ReturnCartLineRequest(
                "S001",
                "SKU-RETRY-RETURN",
                "REF-RETRY-RETURN",
                "Retry Return Tea",
                "9300014",
                "ITEM-RETRY-RETURN",
                null,
                1m,
                10m,
                PriceSourceKind.StoreRetailPrice,
                "StoreRetailPrice",
                "return-source-retry",
                Guid.NewGuid(),
                Guid.NewGuid()));
        }
        else
        {
            cart.AddItem(CreateItem("SKU-RETRY-SALE", "Retry Sale Tea", "9300015", 10m));
        }

        var orderGuid = Guid.NewGuid();
        var workflow = new AuditPaymentWorkflow
        {
            CompletePaymentException = new PaymentUploadFailedException(orderGuid, expectedActualAmount, 0m, "first upload failed"),
            RetryVoucherUploadException = new PaymentUploadFailedException(orderGuid, expectedActualAmount, 0m, "retry upload failed")
        };
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        logger.Events.Clear();

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal(expectedOperationType, auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("UPLOAD_RETRY_FAILED", auditEvent.ReasonCode);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(expectedActualAmount, auditEvent.PaymentAmount);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal(expectedActualAmount, auditEvent.BeforeActual);
        Assert.Equal(expectedActualAmount, auditEvent.AfterActual);
        Assert.Equal(0m, auditEvent.AmountDelta);
        var item = Assert.Single(auditEvent.Items);
        Assert.Equal(expectedQuantityAmount, item.BeforeQuantity);
        Assert.Equal(expectedQuantityAmount, item.AfterQuantity);
        Assert.Equal(0m, item.QuantityDelta);
        Assert.Equal(expectedActualAmount, item.BeforeActualAmount);
        Assert.Equal(expectedActualAmount, item.AfterActualAmount);
        Assert.Equal(0m, item.ActualAmountDelta);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
        Assert.False(cart.IsEmpty);
    }

    [Fact]
    public async Task Payment_upload_retry_technical_exception_records_failed_without_propagating()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-RETRY-ERROR", "Retry Error Tea", "9300016", 10m));
        var orderGuid = Guid.NewGuid();
        var workflow = new AuditPaymentWorkflow
        {
            CompletePaymentException = new PaymentUploadFailedException(orderGuid, 10m, 0m, "first upload failed"),
            RetryVoucherUploadException = new InvalidOperationException("retry unavailable")
        };
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        logger.Events.Clear();

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("UPLOAD_RETRY_FAILED", auditEvent.ReasonCode);
        Assert.Equal("Cash", auditEvent.PaymentMethod);
        Assert.Equal(10m, auditEvent.PaymentAmount);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Single(auditEvent.Items);
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.CorrelationId));
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
        Assert.False(cart.IsEmpty);
    }

    [Fact]
    public async Task Payment_completion_task_canceled_records_failed_without_propagating()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "COMPLETE-TIMEOUT", 10m);
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow { CompletePaymentException = new TaskCanceledException("checkout timeout") },
            Session,
            operationAuditLogger: logger);
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        logger.Events.Clear();

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("TECHNICAL_FAILURE", auditEvent.ReasonCode);
        Assert.Equal(10m, auditEvent.PaymentAmount);
        Assert.Single(auditEvent.Items);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Payment_upload_retry_task_canceled_records_failed_without_propagating()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "RETRY-TIMEOUT", 10m);
        var orderGuid = Guid.NewGuid();
        var workflow = new AuditPaymentWorkflow
        {
            CompletePaymentException = new PaymentUploadFailedException(orderGuid, 10m, 0m, "first upload failed"),
            RetryVoucherUploadException = new TaskCanceledException("retry timeout")
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session, operationAuditLogger: logger);
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        logger.Events.Clear();

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("UPLOAD_RETRY_FAILED", auditEvent.ReasonCode);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Single(auditEvent.Items);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "OperationAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Payment_installment_repayment_task_canceled_records_failed_and_shared_trace()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var order = CreateInstallmentOrder(30m);
        var viewModel = new PaymentViewModel(
            new PosCartService(),
            new AuditPaymentWorkflow(),
            Session,
            installmentOrderService: new ThrowingInstallmentOrderService(
                repaymentException: new TaskCanceledException("repayment timeout")),
            operationAuditLogger: logger);
        viewModel.PrepareForInstallmentRepayment(Session, order);
        viewModel.TenderAmountText = "30";
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        logger.Events.Clear();

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("INSTALLMENT_REPAYMENT_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal(30m, auditEvent.PaymentAmount);
        Assert.Equal(order.OrderId.ToString("D"), auditEvent.OrderGuid);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "InstallmentAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Payment_installment_create_task_canceled_records_failed_and_shared_trace()
    {
        var logger = new RecordingOperationAuditLogger();
        var systemLog = new RecordingApplicationLogSink();
        var cart = CreatePaymentCart(isRefund: false, "INSTALLMENT-TIMEOUT", 60m);
        var viewModel = new PaymentViewModel(
            cart,
            new AuditPaymentWorkflow(),
            Session,
            installmentOrderService: new ThrowingInstallmentOrderService(
                createException: new TaskCanceledException("create timeout")),
            operationAuditLogger: logger)
        {
            IsInstallmentPaymentEnabled = true,
            InstallmentCustomerName = "Customer",
            InstallmentCustomerPhone = "0400000000",
            TenderAmountText = "20"
        };
        await viewModel.SelectCashCommand.ExecuteAsync(null);
        logger.Events.Clear();

        ConsoleLog.ConfigureCenterSink(systemLog);
        try
        {
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => viewModel.ConfirmPaymentCommand.ExecuteAsync(null));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("INSTALLMENT_CREATE", auditEvent.ReasonCode);
        Assert.Equal("Installment+Cash", auditEvent.PaymentMethod);
        Assert.Equal(20m, auditEvent.PaymentAmount);
        var systemEvent = Assert.Single(systemLog.Entries, entry => entry.Category == "InstallmentAudit");
        Assert.Equal(auditEvent.TraceId, systemEvent.TraceId);
    }

    [Fact]
    public async Task Manual_card_cancel_records_payment_cancel_after_cancellation_is_requested()
    {
        var logger = new RecordingOperationAuditLogger();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-CANCEL", "Cancel Tea", "9300006", 10m));
        var workflow = new AuditPaymentWorkflow { BlockCardTenderUntilCancellation = true };
        var viewModel = new PaymentViewModel(
            cart,
            workflow,
            Session,
            operationAuditLogger: logger);

        var addCardTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        for (var attempt = 0; attempt < 100 && !viewModel.IsCardPaymentInProgress; attempt++)
        {
            await Task.Delay(5);
        }

        Assert.True(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        viewModel.CancelCommand.Execute(null);
        await addCardTask;

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("PAYMENT_CANCEL", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("CARD_MANUAL_CANCEL", auditEvent.ReasonCode);
        Assert.Equal("Card", auditEvent.PaymentMethod);
    }

    private static PosSessionState Session => new(
        "HB POS",
        "S001",
        "Main Store",
        "POS-01",
        "C001",
        "Alice",
        true,
        0);

    private static SellableItemDto CreateItem(
        string productCode,
        string displayName,
        string lookupCode,
        decimal retailPrice)
    {
        return new SellableItemDto(
            "S001",
            productCode,
            "REF-" + productCode,
            displayName,
            lookupCode,
            "ITEM-" + productCode,
            null,
            retailPrice,
            PriceSourceKind.StoreRetailPrice,
            "StoreRetailPrice",
            1m,
            DateTimeOffset.UtcNow);
    }

    private static PosCartService CreatePaymentCart(bool isRefund, string suffix, decimal amount)
    {
        var cart = new PosCartService();
        if (!isRefund)
        {
            cart.AddItem(CreateItem($"SKU-{suffix}", $"{suffix} Tea", $"BAR-{suffix}", amount));
            return cart;
        }

        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            $"SKU-{suffix}",
            $"REF-{suffix}",
            $"{suffix} Tea",
            $"BAR-{suffix}",
            $"ITEM-{suffix}",
            null,
            1m,
            amount,
            PriceSourceKind.StoreRetailPrice,
            "StoreRetailPrice",
            $"return-source-{suffix}",
            Guid.NewGuid(),
            Guid.NewGuid()));
        return cart;
    }

    private static InstallmentOrderSummary CreateInstallmentOrder(decimal outstandingAmount)
    {
        return new InstallmentOrderSummary(
            Guid.NewGuid(),
            "IO-AUDIT",
            "Customer",
            "0400000000",
            60m,
            30m,
            30m,
            outstandingAmount,
            0,
            CanAddRepayment: true,
            CanConfirmPickup: false,
            CanCancelRefund: true,
            CanVoid: true,
            "待补款",
            "POS-01",
            DateTimeOffset.UtcNow);
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private sealed class ThrowingOperationAuditLogger : IOperationAuditLogger
    {
        public void Record(OperationAuditEventDto auditEvent)
        {
            throw new InvalidOperationException("logger unavailable");
        }
    }

    private sealed class SingleLinePromotionEvaluationService(decimal discountAmount) : IPromotionEvaluationService
    {
        public Task<IReadOnlyList<PromotionLineDiscount>> EvaluateAsync(
            IReadOnlyList<CartLine> lines,
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            var line = Assert.Single(lines);
            return Task.FromResult<IReadOnlyList<PromotionLineDiscount>>([new PromotionLineDiscount(line, discountAmount)]);
        }
    }

    private sealed class AuditPaymentWorkflow : ICashPaymentWorkflowService
    {
        public bool BlockCardTenderUntilCancellation { get; init; }

        public bool FailTender { get; set; }

        public bool ReleaseVoucherTenderResult { get; init; } = true;

        public Exception? CompletePaymentException { get; init; }

        public Exception? RetryVoucherUploadException { get; init; }

        public Exception? AddTenderException { get; init; }

        public Exception? ReleaseVoucherTenderException { get; init; }

        public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
        {
            return decimal.TryParse(amountTenderedText, out tenderedAmount);
        }

        public decimal CalculateChange(string? amountTenderedText, decimal actualAmount) => 0m;

        public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders) => tenders.Sum(tender => tender.Amount);

        public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
        {
            return actualAmount - tenders.Sum(tender => tender.Amount);
        }

        public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount) => 0m;

        public async Task<PaymentTenderAttemptResult> AddTenderAsync(
            PaymentMethodKind method,
            PosSessionState session,
            decimal actualAmount,
            IReadOnlyList<PaymentTender> currentTenders,
            string? amountText,
            string? referenceText = null,
            CancellationToken cancellationToken = default,
            PosCartSnapshot? cartSnapshot = null)
        {
            if (AddTenderException is not null)
            {
                throw AddTenderException;
            }

            if (BlockCardTenderUntilCancellation && method == PaymentMethodKind.Card)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailTender)
            {
                return PaymentTenderAttemptResult.Fail("payment.status.declined", "Tender declined");
            }

            var amount = decimal.Parse(amountText!);
            if (actualAmount < 0m)
            {
                amount = -amount;
            }
            return PaymentTenderAttemptResult.Success(new PaymentTender(method, amount), "payment.status.tenderAdded");
        }

        public Task<CashPaymentWorkflowResult> CompleteAsync(
            PosCartService cart,
            PosSessionState session,
            string? amountTenderedText,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ReleaseVoucherTenderAsync(
            PaymentTender tender,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            if (ReleaseVoucherTenderException is not null)
            {
                return Task.FromException<bool>(ReleaseVoucherTenderException);
            }

            return Task.FromResult(ReleaseVoucherTenderResult);
        }

        public Task<CashPaymentWorkflowResult> CompletePaymentAsync(
            PosCartService cart,
            PosSessionState session,
            IReadOnlyList<PaymentTender> tenders,
            decimal cashTenderedAmount,
            CancellationToken cancellationToken = default)
        {
            if (CompletePaymentException is not null)
            {
                return Task.FromException<CashPaymentWorkflowResult>(CompletePaymentException);
            }

            var snapshot = cart.CreateSnapshot();
            var totalAmount = cart.TotalAmount;
            var discountAmount = cart.DiscountAmount;
            var actualAmount = cart.ActualAmount;
            var order = new LocalOrder(
                Guid.NewGuid(),
                session.StoreCode,
                session.DeviceCode,
                session.CashierId,
                session.CashierName,
                DateTimeOffset.UtcNow,
                totalAmount,
                discountAmount,
                actualAmount,
                snapshot.Lines.Select(line => new LocalOrderLine(
                    Guid.NewGuid(),
                    line.ProductCode,
                    line.ReferenceCode,
                    line.DisplayName,
                    line.LookupCode,
                    line.ItemNumber,
                    line.Quantity,
                    line.UnitPrice,
                    line.DiscountAmount,
                    decimal.Round(
                        (line.Kind == CartLineKind.Return ? -1m : 1m) * (line.Quantity * line.UnitPrice - line.DiscountAmount),
                        2,
                        MidpointRounding.AwayFromZero),
                    line.PriceSource,
                    line.Kind == CartLineKind.Return ? OrderLineKind.Return : OrderLineKind.Sale)).ToList(),
                tenders.Select(tender => new LocalPayment(Guid.NewGuid(), tender.Method, tender.Amount, tender.Reference)).ToList());
            cart.Clear();
            return Task.FromResult(new CashPaymentWorkflowResult(order, cashTenderedAmount, 0m, 0, session));
        }

        public Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
            Guid orderGuid,
            PosCartService cart,
            PosSessionState session,
            decimal tenderedAmount,
            decimal changeAmount,
            CancellationToken cancellationToken = default)
        {
            return RetryVoucherUploadException is null
                ? Task.FromException<CashPaymentWorkflowResult>(new NotSupportedException())
                : Task.FromException<CashPaymentWorkflowResult>(RetryVoucherUploadException);
        }
    }

    private sealed class ThrowingInstallmentOrderService(
        Exception? repaymentException = null,
        Exception? createException = null) : IInstallmentOrderService
    {
        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(
            PosSessionState session,
            string? keyword,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(
            Guid installmentGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalInstallmentOrder?>(null);

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(
            PosSessionState session,
            InstallmentCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(
            PosSessionState session,
            InstallmentAppendPaymentRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(
            PosSessionState session,
            InstallmentConfirmPickupRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(
            PosSessionState session,
            InstallmentCancelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(
            PosSessionState session,
            InstallmentVoidRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(
            InstallmentOrderCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<InstallmentOrderCreateResult>(
                createException ?? new InvalidOperationException("create unavailable"));

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(
            InstallmentOrderRepaymentRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<InstallmentOrderActionResult>(
                repaymentException ?? new InvalidOperationException("repayment unavailable"));

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(
            Guid orderId,
            PosSessionState session,
            InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> VoidCancelAsync(
            Guid orderId,
            PosSessionState session,
            string? reason = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(
            Guid orderId,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingApplicationLogSink : IApplicationLogSink
    {
        public List<ApplicationLogEntry> Entries { get; } = [];

        public void Enqueue(ApplicationLogEntry entry)
        {
            Entries.Add(entry);
        }
    }
}
