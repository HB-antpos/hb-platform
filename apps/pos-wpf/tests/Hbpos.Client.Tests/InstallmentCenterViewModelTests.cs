using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[Collection(GlobalLoggingTestCollection.Name)]
public sealed class InstallmentCenterViewModelTests
{
    [Fact]
    public async Task CreateInstallmentCommand_opens_create_screen_for_offline_current_cart()
    {
        PosCartServiceSnapshot? capturedSnapshot = null;
        var viewModel = new InstallmentCenterViewModel(
            new FakeInstallmentOrderService(),
            CreateSession() with { IsOnline = false },
            snapshot =>
            {
                capturedSnapshot = snapshot;
                return Task.CompletedTask;
            },
            () => { });
        var cartSnapshot = CreateCartSnapshot();

        viewModel.Prepare(viewModel.Session, cartSnapshot);

        Assert.True(viewModel.IsOffline);
        Assert.True(viewModel.CreateInstallmentCommand.CanExecute(null));

        await viewModel.CreateInstallmentCommand.ExecuteAsync(null);

        Assert.Same(cartSnapshot, capturedSnapshot);
    }

    [Fact]
    public void PaymentMethodOptions_refresh_when_language_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new InstallmentCenterViewModel(
            new FakeInstallmentOrderService(),
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            localization);

        Assert.Equal(
            [PaymentMethodKind.Cash, PaymentMethodKind.Card, PaymentMethodKind.Voucher],
            viewModel.PaymentMethodOptions.Select(option => option.Method).ToArray());
        Assert.Equal(["Cash", "Credit/Debit Card", "Voucher"], viewModel.PaymentMethodOptions.Select(option => option.DisplayName).ToArray());

        viewModel.RepaymentMethod = PaymentMethodKind.Card;
        localization.SetCulture("zh-CN");

        Assert.Equal(
            [PaymentMethodKind.Cash, PaymentMethodKind.Card, PaymentMethodKind.Voucher],
            viewModel.PaymentMethodOptions.Select(option => option.Method).ToArray());
        Assert.Equal(PaymentMethodKind.Card, viewModel.RepaymentMethod);
        Assert.Equal(["现金", "信用/储蓄卡", "代金券"], viewModel.PaymentMethodOptions.Select(option => option.DisplayName).ToArray());
        Assert.Equal("分期中心", viewModel.PageTitleText);
        Assert.Equal("请选择要创建或处理的分期单。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SearchAsync_filters_orders_by_keyword()
    {
        var service = new FakeInstallmentOrderService
        {
            Orders =
            [
                CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true, canCancelWithRefund: true, canVoidCancel: true),
                CreateOrder("IO-002", "李四", "0400222333", "待提货", canConfirmPickup: true)
            ]
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.SearchText = "李四";
        await viewModel.SearchAsync();

        Assert.Single(viewModel.Orders);
        Assert.Equal("IO-002", viewModel.Orders[0].OrderNumber);
        Assert.Equal("李四", viewModel.SelectedOrder!.CustomerName);

        viewModel.SearchText = "0400111222";
        await viewModel.SearchAsync();

        Assert.Single(viewModel.Orders);
        Assert.Equal("IO-001", viewModel.Orders[0].OrderNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_and_recovery_commands_handle_recovery_timeout_and_keep_installment_locked(bool executeRecoveryCommand)
    {
        var targetOrder = CreateOrder("IO-TIMEOUT", "张三", "0400111222", "恢复中", canAddRepayment: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            LockedInstallments = new HashSet<Guid> { targetOrder.OrderId },
            RecoverPendingOperationsException = new TaskCanceledException("recovery timed out")
        };
        var viewModel = new InstallmentCenterViewModel(service, CreateSession(), _ => Task.CompletedTask, () => { });

        var command = executeRecoveryCommand ? viewModel.RecoveryCommand : viewModel.LoadCommand;
        await command.ExecuteAsync(null);

        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.Contains("恢复超时", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("保持锁定", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请勿重复收款", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_fails_closed_when_recovery_and_lock_refresh_both_time_out()
    {
        var targetOrder = CreateOrder("IO-LOCK-UNKNOWN", "张三", "0400111222", "恢复中", canAddRepayment: true);
        var createScreenOpened = false;
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            RecoverPendingOperationsException = new TaskCanceledException("recovery timed out"),
            GetLockedInstallmentsException = new TaskCanceledException("lock lookup timed out")
        };
        var session = CreateSession();
        var viewModel = new InstallmentCenterViewModel(
            service,
            session,
            _ =>
            {
                createScreenOpened = true;
                return Task.CompletedTask;
            },
            () => { });
        viewModel.Prepare(session, CreateCartSnapshot());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.False(viewModel.CreateInstallmentCommand.CanExecute(null));
        Assert.False(viewModel.AddRepaymentCommand.CanExecute(null));
        Assert.Contains("恢复超时", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("保持锁定", viewModel.StatusMessage, StringComparison.Ordinal);

        await viewModel.CreateInstallmentCommand.ExecuteAsync(null);
        Assert.False(createScreenOpened);
    }

    [Fact]
    public async Task Load_keeps_all_write_commands_locked_when_recovery_times_out_and_lock_query_is_empty()
    {
        var mutableOrder = CreateOrder(
            "IO-RECOVERY-UNKNOWN",
            "张三",
            "0400111222",
            "恢复中",
            canAddRepayment: true,
            canCancelWithRefund: true,
            canVoidCancel: true);
        var pickupOrder = CreateOrder(
            "IO-PICKUP-UNKNOWN",
            "李四",
            "0400222333",
            "待提货",
            canConfirmPickup: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [mutableOrder, pickupOrder],
            RecoverPendingOperationsException = new TaskCanceledException("recovery timed out"),
            LockedInstallments = new HashSet<Guid>()
        };
        var session = CreateSession();
        var createScreenOpened = false;
        var viewModel = new InstallmentCenterViewModel(
            service,
            session,
            _ =>
            {
                createScreenOpened = true;
                return Task.CompletedTask;
            },
            () => { });
        viewModel.Prepare(session, CreateCartSnapshot());

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.False(viewModel.CreateInstallmentCommand.CanExecute(null));
        Assert.False(viewModel.AddRepaymentCommand.CanExecute(null));
        Assert.False(viewModel.CancelWithRefundCommand.CanExecute(null));
        Assert.False(viewModel.VoidCancelCommand.CanExecute(null));

        viewModel.SelectedOrder = pickupOrder;

        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
        await viewModel.CreateInstallmentCommand.ExecuteAsync(null);
        await viewModel.VoidCancelCommand.ExecuteAsync(null);
        await viewModel.ConfirmPickupCommand.ExecuteAsync(null);
        Assert.False(createScreenOpened);
        Assert.Equal(Guid.Empty, service.LastVoidOrderId);
        Assert.Null(service.LastConfirmPickupOrderId);
    }

    [Fact]
    public async Task Load_preserves_ordinary_recovery_failure_warning_and_local_lock()
    {
        var targetOrder = CreateOrder("IO-RECOVERY-FAILED", "张三", "0400111222", "恢复中", canAddRepayment: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            LockedInstallments = new HashSet<Guid> { targetOrder.OrderId },
            RecoverPendingOperationsException = new HttpRequestException("recovery unavailable")
        };
        var viewModel = new InstallmentCenterViewModel(service, CreateSession(), _ => Task.CompletedTask, () => { });

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.False(viewModel.AddRepaymentCommand.CanExecute(null));
        Assert.Contains("恢复失败", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请勿重复收款", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRepaymentCommand_requires_voucher_inputs_and_invokes_service()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true, canCancelWithRefund: true, canVoidCancel: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            AddRepaymentResult = new InstallmentOrderActionResult(true, "补款完成")
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;

        Assert.Contains(viewModel.PaymentMethodOptions, option => option.Method == PaymentMethodKind.Cash);
        Assert.Contains(viewModel.PaymentMethodOptions, option => option.Method == PaymentMethodKind.Card);
        Assert.Contains(viewModel.PaymentMethodOptions, option => option.Method == PaymentMethodKind.Voucher);

        viewModel.RepaymentMethod = PaymentMethodKind.Card;
        viewModel.RepaymentReference = string.Empty;

        Assert.True(viewModel.AddRepaymentCommand.CanExecute(null));

        viewModel.RepaymentMethod = PaymentMethodKind.Voucher;
        viewModel.RepaymentReference = "VIP001";

        Assert.False(viewModel.AddRepaymentCommand.CanExecute(null));

        viewModel.RepaymentVoucherToken = "LOCK-001";

        Assert.True(viewModel.AddRepaymentCommand.CanExecute(null));

        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRepaymentRequest);
        Assert.Equal(targetOrder.OrderId, service.LastRepaymentRequest!.InstallmentGuid);
        Assert.Equal(40m, service.LastRepaymentRequest.Payment.Amount);
        Assert.Equal(PaymentMethodKind.Voucher, service.LastRepaymentRequest.Payment.Method);
        Assert.Equal("VIP001", service.LastRepaymentRequest.Payment.Reference);
        Assert.Equal("LOCK-001", service.LastRepaymentRequest.Payment.ReservationToken);
        Assert.Equal("补款完成", viewModel.StatusMessage);
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("INSTALLMENT_REPAYMENT_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Voucher", auditEvent.PaymentMethod);
        Assert.Equal(40m, auditEvent.PaymentAmount);
    }

    [Fact]
    public async Task AddRepaymentCommand_maps_service_exception_to_status_message()
    {
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            ThrowOnRepayment = true
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;
        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.Equal("API refused repayment", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AddRepaymentCommand_delegates_card_repayment_to_restartable_operation_service()
    {
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            AddRepaymentResult = new InstallmentOrderActionResult(true, "补款完成")
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            cardTerminalClient: new StubCardTerminalClient(authorizeException: new InvalidOperationException("the page must not authorize cards")));

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;
        viewModel.RepaymentMethod = PaymentMethodKind.Card;

        Assert.True(viewModel.AddRepaymentCommand.CanExecute(null));

        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRepaymentRequest);
        Assert.Equal(PaymentMethodKind.Card, service.LastRepaymentRequest!.Payment.Method);
        Assert.Null(service.LastRepaymentRequest.Payment.Reference);
        Assert.Null(service.LastRepaymentRequest.Payment.CardTransactions);
    }

    [Fact]
    public async Task AddRepaymentCommand_does_not_invoke_the_page_card_terminal()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-CARD-DENIED", "张三", "0400111222", "待补款", canAddRepayment: true);
        var service = new FakeInstallmentOrderService { Orders = [targetOrder], AddRepaymentResult = new InstallmentOrderActionResult(true, "已交由分期操作服务处理") };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            cardTerminalClient: new StubCardTerminalClient(authorizeException: new InvalidOperationException("the page must not authorize cards")),
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;
        viewModel.RepaymentMethod = PaymentMethodKind.Card;
        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRepaymentRequest);
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("INSTALLMENT_REPAYMENT_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("REPAYMENT", auditEvent.ReasonCode);
        Assert.Equal("Card", auditEvent.PaymentMethod);
        Assert.Equal(40m, auditEvent.PaymentAmount);
        Assert.Equal(targetOrder.OrderId.ToString("D"), auditEvent.OrderGuid);
        Assert.Null(auditEvent.SafeMessage);
    }

    [Fact]
    public async Task AddRepaymentCommand_records_service_failure_without_page_terminal_exception()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-CARD-ERROR", "张三", "0400111222", "待补款", canAddRepayment: true);
        var service = new FakeInstallmentOrderService { Orders = [targetOrder], AddRepaymentResult = new InstallmentOrderActionResult(false, "等待恢复", RequiresReview: true) };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            cardTerminalClient: new StubCardTerminalClient(authorizeException: new InvalidOperationException("terminal unavailable")),
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;
        viewModel.RepaymentMethod = PaymentMethodKind.Card;

        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRepaymentRequest);
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("INSTALLMENT_REPAYMENT_COMPLETE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("REPAYMENT", auditEvent.ReasonCode);
        Assert.Equal("等待恢复", auditEvent.SafeMessage);
    }

    [Fact]
    public async Task Cancel_refund_and_void_commands_follow_button_state_and_invoke_service()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true, canCancelWithRefund: true, canVoidCancel: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            CancelWithRefundResult = new InstallmentOrderActionResult(true, "已取消退款"),
            VoidCancelResult = new InstallmentOrderActionResult(true, "已作废")
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();

        Assert.True(viewModel.CancelWithRefundCommand.CanExecute(null));
        Assert.True(viewModel.VoidCancelCommand.CanExecute(null));

        viewModel.VoidReason = "客户改主意";
        await viewModel.CancelWithRefundCommand.ExecuteAsync(null);
        await viewModel.VoidCancelCommand.ExecuteAsync(null);

        Assert.Equal(targetOrder.OrderId, service.LastCancelOrderId);
        Assert.Equal(targetOrder.OrderId, service.LastVoidOrderId);
        Assert.Equal(
            ["INSTALLMENT_REPAYMENT_CANCEL", "INSTALLMENT_REPAYMENT_CANCEL"],
            auditLogger.Events.Select(auditEvent => auditEvent.OperationType));
        Assert.Equal(
            ["CANCEL_WITH_REFUND", "VOID"],
            auditLogger.Events.Select(auditEvent => auditEvent.ReasonCode));
        Assert.Equal("客户改主意", service.LastVoidReason);
    }

    [Fact]
    public async Task ConfirmPickupCommand_follows_button_state_and_invokes_service()
    {
        var targetOrder = CreateOrder("IO-002", "李四", "0400222333", "待提货", canConfirmPickup: true);
        var service = new FakeInstallmentOrderService { Orders = [targetOrder] };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsConfirmPickupEnabled);
        Assert.True(viewModel.ConfirmPickupCommand.CanExecute(null));

        await viewModel.ConfirmPickupCommand.ExecuteAsync(null);

        Assert.Equal(targetOrder.OrderId, service.LastConfirmPickupOrderId);

        viewModel.Prepare(CreateSession() with { IsOnline = false }, null);

        Assert.False(viewModel.IsConfirmPickupEnabled);
        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConfirmPickupCommand_records_installment_pickup_operation_audit()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-002", "李四", "0400222333", "待提货", canConfirmPickup: true);
        var service = new FakeInstallmentOrderService { Orders = [targetOrder] };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        await viewModel.ConfirmPickupCommand.ExecuteAsync(null);

        // 确认提货把分期订单推进到终态（货物交给顾客），必须留下审计。
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("INSTALLMENT_PICKUP_CONFIRM", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("PICKUP", auditEvent.ReasonCode);
        Assert.Equal(targetOrder.OrderId.ToString("D"), auditEvent.OrderGuid);
    }

    [Fact]
    public async Task ConfirmPickup_unknown_result_immediately_locks_the_selected_order()
    {
        var targetOrder = CreateOrder("IO-PICKUP-UNKNOWN", "李四", "0400222333", "待提货", canConfirmPickup: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            ConfirmPickupResult = new InstallmentOrderActionResult(
                false,
                "提货确认结果未知",
                RequiresReview: true)
        };
        var viewModel = new InstallmentCenterViewModel(service, CreateSession(), _ => Task.CompletedTask, () => { });

        await viewModel.LoadAsync();
        await viewModel.ConfirmPickupCommand.ExecuteAsync(null);

        Assert.Equal(targetOrder.OrderId, service.LastConfirmPickupOrderId);
        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Supervisor_refund_resolution_records_reason_evidence_and_has_no_ordinary_retry_path()
    {
        var targetOrder = CreateOrder("IO-REFUND-UNKNOWN", "张三", "0400111222", "退款未知", canCancelWithRefund: true);
        var step = new LocalInstallmentRefundStep(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            PaymentMethodKind.Card,
            40m,
            "TXN-001",
            "refund-key",
            LocalInstallmentRefundStepState.ResultUnknown,
            null, null, "未知", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            LockedInstallments = new HashSet<Guid> { targetOrder.OrderId },
            RefundStepsForReview = [step],
            ResumeCancelResult = new InstallmentOrderActionResult(false, "退款保持锁定", RequiresReview: true)
        };
        var session = CreateSession() with
        {
            CashierSession = CreateCashierSession(Permissions.PosTerminal.Installments.Cancel)
        };
        var viewModel = new InstallmentCenterViewModel(service, session, _ => Task.CompletedTask, () => { });

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasRefundStepsForReview);
        Assert.True(viewModel.SupervisorResolveRefundCommand.CanExecute(null));
        viewModel.SupervisorRefundDecision = InstallmentRefundSupervisorDecision.ConfirmNotRefunded;
        viewModel.SupervisorRefundReason = "银行明确确认未退款";
        viewModel.SupervisorRefundEvidence = "bank-case-123";
        await viewModel.SupervisorResolveRefundCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastSupervisorResolution);
        Assert.Equal("C001", service.LastSupervisorResolution!.OperatorId);
        Assert.Equal("bank-case-123", service.LastSupervisorResolution.Evidence);
        Assert.Equal(step.OperationGuid, service.LastResumeOperationGuid);
    }

    [Fact]
    public async Task Supervisor_refund_resolution_handles_recovery_timeout_after_decision_is_saved()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-REFUND-TIMEOUT", "张三", "0400111222", "退款未知", canCancelWithRefund: true);
        var step = new LocalInstallmentRefundStep(
            Guid.NewGuid(),
            Guid.NewGuid(),
            targetOrder.OrderId,
            PaymentMethodKind.Card,
            40m,
            "TXN-001",
            "refund-key",
            LocalInstallmentRefundStepState.ResultUnknown,
            null, null, "未知", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            LockedInstallments = new HashSet<Guid> { targetOrder.OrderId },
            RefundStepsForReview = [step],
            ResumeCancelException = new TaskCanceledException("recovery query timed out")
        };
        var session = CreateSession() with
        {
            CashierSession = CreateCashierSession(Permissions.PosTerminal.Installments.Cancel)
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            session,
            _ => Task.CompletedTask,
            () => { },
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        viewModel.SupervisorRefundDecision = InstallmentRefundSupervisorDecision.ConfirmNotRefunded;
        viewModel.SupervisorRefundReason = "银行确认未退款";
        await viewModel.SupervisorResolveRefundCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastSupervisorResolution);
        Assert.Equal(step.OperationGuid, service.LastResumeOperationGuid);
        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.Contains("裁决已保存", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("恢复查询超时", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("保持锁定", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("刷新核对", viewModel.StatusMessage, StringComparison.Ordinal);
        var failureAudit = Assert.Single(auditLogger.Events.Where(auditEvent => auditEvent.Outcome == "SupervisorRecoveryFailed"));
        Assert.Equal("TaskCanceledException", failureAudit.SafeMessage);
    }

    [Fact]
    public async Task Supervisor_refund_resolution_handles_timeout_while_decision_result_is_unknown()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var targetOrder = CreateOrder("IO-RESOLUTION-TIMEOUT", "张三", "0400111222", "退款未知", canCancelWithRefund: true);
        var step = new LocalInstallmentRefundStep(
            Guid.NewGuid(),
            Guid.NewGuid(),
            targetOrder.OrderId,
            PaymentMethodKind.Card,
            40m,
            "TXN-002",
            "refund-timeout-key",
            LocalInstallmentRefundStepState.ResultUnknown,
            null, null, "未知", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            LockedInstallments = new HashSet<Guid> { targetOrder.OrderId },
            RefundStepsForReview = [step],
            ResolveRefundStepException = new TaskCanceledException("resolution request timed out")
        };
        var session = CreateSession() with
        {
            CashierSession = CreateCashierSession(Permissions.PosTerminal.Installments.Cancel)
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            session,
            _ => Task.CompletedTask,
            () => { },
            operationAuditLogger: auditLogger);

        await viewModel.LoadAsync();
        viewModel.SupervisorRefundDecision = InstallmentRefundSupervisorDecision.ConfirmNotRefunded;
        viewModel.SupervisorRefundReason = "银行确认未退款";
        await viewModel.SupervisorResolveRefundCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastSupervisorResolution);
        Assert.Null(service.LastResumeOperationGuid);
        Assert.True(viewModel.IsSelectedOrderLocked);
        Assert.Contains("裁决请求超时", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("结果未知", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("保持锁定", viewModel.StatusMessage, StringComparison.Ordinal);
        var unknownAudit = Assert.Single(auditLogger.Events.Where(auditEvent => auditEvent.Outcome == "SupervisorResolutionUnknown"));
        Assert.Equal("TaskCanceledException", unknownAudit.SafeMessage);
    }

    [Fact]
    public async Task Refund_step_refresh_ignores_stale_result_after_selected_order_changes()
    {
        var firstOrder = CreateOrder("IO-REFUND-A", "张三", "0400111222", "退款未知", canCancelWithRefund: true);
        var secondOrder = CreateOrder("IO-REFUND-B", "李四", "0400222333", "退款未知", canCancelWithRefund: true);
        var firstStep = CreateRefundStep(firstOrder.OrderId, "refund-a");
        var secondStep = CreateRefundStep(secondOrder.OrderId, "refund-b");
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestGate = new TaskCompletionSource<IReadOnlyList<LocalInstallmentRefundStep>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeInstallmentOrderService
        {
            Orders = [firstOrder, secondOrder],
            GetRefundStepsForReviewHandler = async (installmentGuid, _) =>
            {
                if (installmentGuid == firstOrder.OrderId)
                {
                    firstRequestStarted.TrySetResult();
                    var result = await firstRequestGate.Task;
                    firstRequestReturned.TrySetResult();
                    return result;
                }

                return [secondStep];
            }
        };
        var viewModel = new InstallmentCenterViewModel(service, CreateSession(), _ => Task.CompletedTask, () => { });
        var staleStepWasApplied = false;
        viewModel.RefundStepsForReview.CollectionChanged += (_, _) =>
        {
            staleStepWasApplied |= viewModel.RefundStepsForReview.Any(step => step.RefundStepGuid == firstStep.RefundStepGuid);
        };

        viewModel.SelectedOrder = firstOrder;
        await firstRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.SelectedOrder = secondOrder;

        Assert.Same(secondStep, Assert.Single(viewModel.RefundStepsForReview));

        firstRequestGate.SetResult([firstStep]);
        await firstRequestReturned.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        for (var index = 0; index < 10; index++)
        {
            await Task.Yield();
        }

        Assert.False(staleStepWasApplied);
        Assert.Same(secondStep, Assert.Single(viewModel.RefundStepsForReview));
        Assert.Same(secondStep, viewModel.SelectedRefundStep);
    }

    [Fact]
    public async Task Switching_to_order_with_pending_refund_query_immediately_clears_previous_steps()
    {
        var firstOrder = CreateOrder("IO-REFUND-A", "张三", "0400111222", "退款未知", canCancelWithRefund: true);
        var secondOrder = CreateOrder("IO-REFUND-B", "李四", "0400222333", "退款未知", canCancelWithRefund: true);
        var firstStep = CreateRefundStep(firstOrder.OrderId, "refund-a");
        var secondRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequestGate = new TaskCompletionSource<IReadOnlyList<LocalInstallmentRefundStep>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeInstallmentOrderService
        {
            Orders = [firstOrder, secondOrder],
            LockedInstallments = new HashSet<Guid> { firstOrder.OrderId, secondOrder.OrderId },
            GetRefundStepsForReviewHandler = (installmentGuid, _) =>
            {
                if (installmentGuid == secondOrder.OrderId)
                {
                    secondRequestStarted.TrySetResult();
                    return secondRequestGate.Task;
                }

                return Task.FromResult<IReadOnlyList<LocalInstallmentRefundStep>>([firstStep]);
            }
        };
        var viewModel = new InstallmentCenterViewModel(service, CreateSession(), _ => Task.CompletedTask, () => { });

        await viewModel.LoadAsync();
        Assert.Same(firstStep, Assert.Single(viewModel.RefundStepsForReview));
        Assert.True(viewModel.SupervisorResolveRefundCommand.CanExecute(null));

        viewModel.SelectedOrder = secondOrder;
        await secondRequestStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Empty(viewModel.RefundStepsForReview);
        Assert.Null(viewModel.SelectedRefundStep);
        Assert.False(viewModel.SupervisorResolveRefundCommand.CanExecute(null));
        secondRequestGate.SetResult([]);
    }

    [Fact]
    public async Task Ordinary_cashier_cannot_see_or_execute_supervisor_refund_resolution()
    {
        var targetOrder = CreateOrder("IO-REFUND-DENIED", "张三", "0400111222", "退款未知", canCancelWithRefund: true);
        var step = new LocalInstallmentRefundStep(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PaymentMethodKind.Card, 40m, "TXN-001", "refund-key", LocalInstallmentRefundStepState.ResultUnknown, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var service = new FakeInstallmentOrderService { Orders = [targetOrder], LockedInstallments = new HashSet<Guid> { targetOrder.OrderId }, RefundStepsForReview = [step] };
        var session = CreateSession() with { CashierSession = CreateCashierSession() };
        var viewModel = new InstallmentCenterViewModel(service, session, _ => Task.CompletedTask, () => { });

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasRefundStepsForReview);
        Assert.False(viewModel.SupervisorResolveRefundCommand.CanExecute(null));
        Assert.Null(service.LastSupervisorResolution);
    }

    [Theory]
    [InlineData("repayment")]
    [InlineData("cancel")]
    public async Task Authorized_write_does_not_follow_selection_to_another_order(string action)
    {
        var firstOrder = CreateOrder("IO-AUTH-A", "张三", "0400111222", "Active", canAddRepayment: true, canCancelWithRefund: true);
        var secondOrder = CreateOrder("IO-AUTH-B", "李四", "0400222333", "Active", canAddRepayment: true, canCancelWithRefund: true);
        var service = new FakeInstallmentOrderService { Orders = [firstOrder, secondOrder] };
        var authorization = new CallbackOperationAuthorizationService();
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            operationAuthorizationService: authorization);
        await viewModel.LoadAsync();
        authorization.BeforeReturn = () => viewModel.SelectedOrder = secondOrder;

        if (action == "repayment")
        {
            await viewModel.AddRepaymentCommand.ExecuteAsync(null);
            Assert.Null(service.LastRepaymentRequest);
        }
        else
        {
            await viewModel.CancelWithRefundCommand.ExecuteAsync(null);
            Assert.Equal(Guid.Empty, service.LastCancelOrderId);
        }
    }

    [Fact]
    public void Dispose_stops_receiving_culture_changed_events()
    {
        var localization = new LocalizationService();
        var viewModel = new InstallmentCenterViewModel(new FakeInstallmentOrderService(), CreateSession(), _ => Task.CompletedTask, () => { }, localization);
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InstallmentCenterViewModel.PageTitleText)) notifications++;
        };

        viewModel.Dispose();
        localization.SetCulture("zh-CN");

        Assert.Equal(0, notifications);
    }

    private static InstallmentOrderSummary CreateOrder(
        string orderNumber,
        string customerName,
        string phone,
        string status,
        bool canAddRepayment = false,
        bool canConfirmPickup = false,
        bool canCancelWithRefund = false,
        bool canVoidCancel = false)
    {
        return new InstallmentOrderSummary(
            Guid.NewGuid(),
            orderNumber,
            customerName,
            phone,
            120m,
            30m,
            canConfirmPickup ? 120m : 30m,
            canConfirmPickup ? 0m : 90m,
            0,
            canAddRepayment,
            canConfirmPickup,
            canCancelWithRefund,
            canVoidCancel,
            status,
            "POS-01",
            DateTimeOffset.Now);
    }

    private static LocalInstallmentRefundStep CreateRefundStep(Guid originalPaymentGuid, string idempotencyKey) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            originalPaymentGuid,
            PaymentMethodKind.Card,
            40m,
            "TXN-REFUND",
            idempotencyKey,
            LocalInstallmentRefundStepState.ResultUnknown,
            null,
            null,
            "未知",
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private static CashierSessionDto CreateCashierSession(params string[] permissions) => new(
        "C001", "user-1", "Alice", "S001", "POS-01", [], permissions, ["S001"], false, false, false);

    private static PosCartServiceSnapshot CreateCartSnapshot()
    {
        return new PosCartServiceSnapshot(
            55m,
            0m,
            55m,
            [
                new PosCartLineServiceSnapshot("SKU-001", null, "Premium Rice Cooker", "690001", "ITEM-001", 1m, 55m, 0m, 55m)
            ]);
    }

    private sealed class FakeInstallmentOrderService : IInstallmentOrderService
    {
        public IReadOnlyList<InstallmentOrderSummary> Orders { get; init; } = [];

        public InstallmentOrderActionResult AddRepaymentResult { get; init; } = new(false, "未配置");

        public InstallmentOrderActionResult CancelWithRefundResult { get; init; } = new(false, "未配置");

        public InstallmentOrderActionResult VoidCancelResult { get; init; } = new(false, "未配置");

        public InstallmentOrderActionResult ConfirmPickupResult { get; init; } = new(true, "已确认提货");

        public bool ThrowOnRepayment { get; init; }

        public IReadOnlySet<Guid> LockedInstallments { get; init; } = new HashSet<Guid>();

        public IReadOnlyList<LocalInstallmentRefundStep> RefundStepsForReview { get; init; } = [];

        public Func<Guid, CancellationToken, Task<IReadOnlyList<LocalInstallmentRefundStep>>>? GetRefundStepsForReviewHandler { get; init; }

        public InstallmentOrderActionResult ResumeCancelResult { get; init; } = new(false, "退款保持锁定", RequiresReview: true);

        public Exception? RecoverPendingOperationsException { get; init; }

        public Exception? GetLockedInstallmentsException { get; init; }

        public Exception? ResumeCancelException { get; init; }

        public Exception? ResolveRefundStepException { get; init; }

        public InstallmentOrderRepaymentRequest? LastRepaymentRequest { get; private set; }

        public Guid LastCancelOrderId { get; private set; }

        public Guid LastVoidOrderId { get; private set; }

        public string? LastVoidReason { get; private set; }

        public Guid? LastConfirmPickupOrderId { get; private set; }

        public InstallmentRefundSupervisorResolution? LastSupervisorResolution { get; private set; }

        public Guid? LastResumeOperationGuid { get; private set; }

        public Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
            GetLockedInstallmentsException is null
                ? Task.FromResult(LockedInstallments)
                : Task.FromException<IReadOnlySet<Guid>>(GetLockedInstallmentsException);

        public Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverPendingOperationsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
            RecoverPendingOperationsException is null
                ? Task.FromResult<IReadOnlyList<InstallmentOperationRecoveryResult>>([])
                : Task.FromException<IReadOnlyList<InstallmentOperationRecoveryResult>>(RecoverPendingOperationsException);

        public Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForReviewAsync(Guid installmentGuid, CancellationToken cancellationToken = default) =>
            GetRefundStepsForReviewHandler?.Invoke(installmentGuid, cancellationToken) ?? Task.FromResult(RefundStepsForReview);

        public Task<bool> ResolveRefundStepAsync(Guid refundStepGuid, InstallmentRefundSupervisorResolution resolution, CancellationToken cancellationToken = default)
        {
            LastSupervisorResolution = resolution;
            return ResolveRefundStepException is null
                ? Task.FromResult(true)
                : Task.FromException<bool>(ResolveRefundStepException);
        }

        public Task<InstallmentOrderActionResult> ResumeCancelAfterSupervisorAsync(Guid operationGuid, string installmentNumber, PosSessionState session, CancellationToken cancellationToken = default)
        {
            LastResumeOperationGuid = operationGuid;
            return ResumeCancelException is null
                ? Task.FromResult(ResumeCancelResult)
                : Task.FromException<InstallmentOrderActionResult>(ResumeCancelException);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Orders);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
        {
            var filtered = string.IsNullOrWhiteSpace(keyword)
                ? Orders
                : Orders.Where(order =>
                    order.OrderNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.Status.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.DeviceCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>(filtered.ToList());
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
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRepayment)
            {
                throw new InvalidOperationException("API refused repayment");
            }

            LastRepaymentRequest = request;
            return Task.FromResult(AddRepaymentResult);
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            LastCancelOrderId = orderId;
            return Task.FromResult(CancelWithRefundResult);
        }

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
        {
            LastVoidOrderId = orderId;
            LastVoidReason = reason;
            return Task.FromResult(VoidCancelResult);
        }

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            LastConfirmPickupOrderId = orderId;
            return Task.FromResult(ConfirmPickupResult);
        }
    }

    private sealed class CallbackOperationAuthorizationService : IOperationAuthorizationService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public Action? BeforeReturn { get; set; }
        public string ScannerPageId => "installment-authorization-test";
        public bool IsPromptOpen => false;
        public bool IsBusy => false;
        public string PromptMessage => string.Empty;
        public string StatusMessage => string.Empty;
        public string PermissionCode => string.Empty;
        public string Screen => string.Empty;
        public string Action => string.Empty;
        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            BeforeReturn?.Invoke();
            return Task.FromResult<OperationAuthorizationScope?>(new OperationAuthorizationScope(
                CreateCashierSession(permissionCode),
                permissionCode,
                screen,
                action));
        }

        public bool ProcessScannerBarcode(string barcode) => false;
        public void Cancel() { }
        public void RevokeAll() { }
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private sealed class ApprovedCardTerminalClient(
        string reference,
        IReadOnlyList<CardTransactionDto>? cardTransactions = null) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount, CardTransactions: cardTransactions));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }

    private sealed class StubCardTerminalClient(
        PaymentAuthorizationResult? authorization = null,
        Exception? authorizeException = null) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            if (authorizeException is not null)
            {
                return Task.FromException<PaymentAuthorizationResult>(authorizeException);
            }

            return Task.FromResult(authorization ?? new PaymentAuthorizationResult(true, "CARD-OK", AuthorizedAmount: amount));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
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
