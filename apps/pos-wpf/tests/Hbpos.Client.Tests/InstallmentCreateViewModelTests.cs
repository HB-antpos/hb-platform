using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class InstallmentCreateViewModelTests
{
    [Fact]
    public void Prepare_sets_cart_details_and_default_down_payment()
    {
        var service = new FakeInstallmentOrderService();
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.Prepare(CreateSession(), CreateCartSnapshot());

        Assert.Equal("Create Installment", viewModel.PageTitleText);
        Assert.Single(viewModel.CartLines);
        Assert.Equal(130m, viewModel.GoodsAmount);
        Assert.Equal(10m, viewModel.DiscountAmount);
        Assert.Equal(120m, viewModel.TotalAmount);
        Assert.Equal(20m, viewModel.DownPaymentAmount);
        Assert.Equal(100m, viewModel.FinancedAmount);
        Assert.Equal("Complete the customer, down payment, and installment details.", viewModel.StatusMessage);
    }

    [Fact]
    public void SubmitCommand_rejects_order_total_below_installment_minimum_with_friendly_status()
    {
        var viewModel = new InstallmentCreateViewModel(
            new FakeInstallmentOrderService(),
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.Prepare(CreateSession(), CreateCartSnapshot(actualAmount: 49.99m));
        viewModel.CustomerName = "Alice";
        viewModel.CustomerPhone = "0400111222";

        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        Assert.Contains("$50", viewModel.DownPaymentStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmitCommand_rejects_down_payment_below_minimum_with_friendly_status()
    {
        var viewModel = new InstallmentCreateViewModel(
            new FakeInstallmentOrderService(),
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.Prepare(CreateSession(), CreateCartSnapshot(actualAmount: 55m));
        viewModel.CustomerName = "Alice";
        viewModel.CustomerPhone = "0400111222";
        viewModel.DownPaymentAmount = 19.99m;

        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        Assert.Contains("$20", viewModel.DownPaymentStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentMethodOptions_refresh_when_language_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new InstallmentCreateViewModel(
            new FakeInstallmentOrderService(),
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            localization);

        Assert.Equal(
            [PaymentMethodKind.Cash, PaymentMethodKind.Card, PaymentMethodKind.Voucher],
            viewModel.PaymentMethodOptions.Select(option => option.Method).ToArray());
        Assert.Equal(["Cash", "Credit/Debit Card", "Voucher"], viewModel.PaymentMethodOptions.Select(option => option.DisplayName).ToArray());

        viewModel.DownPaymentMethod = PaymentMethodKind.Voucher;
        localization.SetCulture("zh-CN");

        Assert.Equal(
            [PaymentMethodKind.Cash, PaymentMethodKind.Card, PaymentMethodKind.Voucher],
            viewModel.PaymentMethodOptions.Select(option => option.Method).ToArray());
        Assert.Equal(PaymentMethodKind.Voucher, viewModel.DownPaymentMethod);
        Assert.Equal(["现金", "信用/储蓄卡", "代金券"], viewModel.PaymentMethodOptions.Select(option => option.DisplayName).ToArray());
        Assert.Equal("创建分期", viewModel.PageTitleText);
    }

    [Fact]
    public void Dispose_stops_receiving_culture_changes()
    {
        var localization = new LocalizationService();
        localization.SetCulture(LocalizationService.DefaultCultureName);
        var viewModel = new InstallmentCreateViewModel(
            new FakeInstallmentOrderService(),
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            localization);
        var changeCount = 0;
        viewModel.PropertyChanged += (_, _) => changeCount++;

        try
        {
            viewModel.Dispose();
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.ChineseCultureName);

            Assert.Equal(0, changeCount);
        }
        finally
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    [Fact]
    public async Task SubmitCommand_requires_voucher_reference_and_token_then_submits_request()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var service = new FakeInstallmentOrderService();
        InstallmentOrderSummary? createdOrder = null;
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            order =>
            {
                createdOrder = order;
                return Task.CompletedTask;
            },
            () => { },
            operationAuditLogger: auditLogger);

        viewModel.Prepare(CreateSession(), CreateCartSnapshot());
        viewModel.CustomerName = "张三";
        viewModel.CustomerPhone = "0400111222";
        viewModel.DownPaymentAmount = 30m;
        viewModel.DownPaymentMethod = PaymentMethodKind.Voucher;
        viewModel.DownPaymentReference = "VIP001";

        Assert.False(viewModel.SubmitCommand.CanExecute(null));

        viewModel.VoucherReservationToken = "LOCK-001";

        Assert.True(viewModel.SubmitCommand.CanExecute(null));

        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastCreateRequest);
        Assert.Equal(PaymentMethodKind.Voucher, service.LastCreateRequest!.DownPayment.Method);
        Assert.Equal("VIP001", service.LastCreateRequest.DownPayment.Reference);
        Assert.Equal("LOCK-001", service.LastCreateRequest.DownPayment.ReservationToken);
        Assert.NotNull(createdOrder);
        Assert.Equal("Installment order created.", viewModel.StatusMessage);
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("SALE_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Installment+Voucher", auditEvent.PaymentMethod);
        Assert.Equal(30m, auditEvent.PaymentAmount);
        Assert.Equal(120m, auditEvent.BeforeActual);
        Assert.Equal(0m, auditEvent.AfterActual);
        Assert.Equal(-120m, auditEvent.AmountDelta);
    }

    [Fact]
    public async Task SubmitCommand_allows_minimum_installment_total_and_down_payment()
    {
        var service = new FakeInstallmentOrderService();
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.Prepare(CreateSession(), CreateCartSnapshot(actualAmount: 55m));
        viewModel.CustomerName = "Alice";
        viewModel.CustomerPhone = "0400111222";
        viewModel.DownPaymentAmount = 20m;

        Assert.True(viewModel.SubmitCommand.CanExecute(null));

        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastCreateRequest);
        Assert.Equal(55m, service.LastCreateRequest!.CartSnapshot.ActualAmount);
        Assert.Equal(20m, service.LastCreateRequest.DownPaymentAmount);
    }

    [Fact]
    public async Task SubmitCommand_reuses_financial_identity_after_safe_exception()
    {
        var service = new FakeInstallmentOrderService
        {
            OnCreateAsync = (request, callCount) => callCount == 1
                ? Task.FromException<InstallmentOrderCreateResult>(new InvalidOperationException("temporary failure"))
                : Task.FromResult(FakeInstallmentOrderService.CreateSucceededResult())
        };
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });
        viewModel.Prepare(CreateSession(), CreateCartSnapshot());
        viewModel.CustomerName = "Alice";
        viewModel.CustomerPhone = "0400111222";

        await viewModel.SubmitCommand.ExecuteAsync(null);
        Assert.True(viewModel.SubmitCommand.CanExecute(null));
        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CreateRequests.Count);
        Assert.Equal(
            service.CreateRequests[0].DownPayment.PaymentGuid,
            service.CreateRequests[1].DownPayment.PaymentGuid);
        Assert.Equal(
            service.CreateRequests[0].DownPayment.IdempotencyKey,
            service.CreateRequests[1].DownPayment.IdempotencyKey);
        Assert.Equal(
            service.CreateRequests[0].InstallmentGuid,
            service.CreateRequests[1].InstallmentGuid);
        Assert.NotEqual(
            service.CreateRequests[0].InstallmentGuid,
            service.CreateRequests[0].DownPayment.PaymentGuid);
    }

    [Fact]
    public async Task SubmitCommand_requires_review_keeps_identity_and_disables_new_create()
    {
        var service = new FakeInstallmentOrderService
        {
            OnCreateAsync = (_, _) => Task.FromResult(
                new InstallmentOrderCreateResult(
                    false,
                    "The previous create result is unknown.",
                    RequiresReview: true))
        };
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });
        viewModel.Prepare(CreateSession(), CreateCartSnapshot());
        viewModel.CustomerName = "Alice";
        viewModel.CustomerPhone = "0400111222";

        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Single(service.CreateRequests);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        viewModel.Prepare(CreateSession(), CreateCartSnapshot());
        viewModel.CustomerName = "Alice";
        viewModel.CustomerPhone = "0400111222";
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private static PosCartServiceSnapshot CreateCartSnapshot(decimal totalAmount = 130m, decimal discountAmount = 10m, decimal actualAmount = 120m)
    {
        return new PosCartServiceSnapshot(
            totalAmount,
            discountAmount,
            actualAmount,
            [
                new PosCartLineServiceSnapshot("SKU-001", null, "Premium Rice Cooker", "690001", "ITEM-001", 1m, totalAmount, discountAmount, actualAmount)
            ]);
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private sealed class FakeInstallmentOrderService : IInstallmentOrderService
    {
        public InstallmentOrderCreateRequest? LastCreateRequest { get; private set; }

        public List<InstallmentOrderCreateRequest> CreateRequests { get; } = [];

        public Func<InstallmentOrderCreateRequest, int, Task<InstallmentOrderCreateResult>>? OnCreateAsync { get; init; }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
        }

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalInstallmentOrder?>(null);
        }

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            CreateRequests.Add(request);
            return OnCreateAsync?.Invoke(request, CreateRequests.Count) ??
                Task.FromResult(CreateSucceededResult());
        }

        public static InstallmentOrderCreateResult CreateSucceededResult() =>
            new(
                true,
                "Installment order created.",
                new InstallmentOrderSummary(
                    Guid.NewGuid(),
                    "IO-001",
                    "张三",
                    "0400111222",
                    120m,
                    30m,
                    30m,
                    90m,
                    0,
                    true,
                    false,
                    true,
                    true,
                    "Pending repayment",
                    "POS-01",
                    DateTimeOffset.Now));

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
