using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Client.Tests;

// 卡退款（Linkly / Square）经恢复服务落单时，草稿里的「退款券」tender 仍是占位引用 VOUCHER_REFUND_PENDING。
// 恢复落单成功后必须复用首次完成路径的同一套签券逻辑（同一幂等键）补签并回写真实券号；
// 签券失败只保留占位，不阻断落单、不改变恢复状态机结果（交给上传侧兜底）。
// 服务均按生产 DI（ServiceCollection + AddSingleton）构造，以同时覆盖 IVoucherTenderClient 的注入。
public sealed partial class CardPaymentRecoveryServiceTests
{
    private const string PendingRefundVoucherReference = "VOUCHER_REFUND_PENDING";
    private const string DraftRefundVoucherKey = "draft-refund-voucher-key-0001";

    [Fact]
    public async Task ResolveRefundAsync_linkly_confirm_refunded_issues_pending_refund_voucher_with_draft_key()
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-RV");
        var attempts = new FakeCardPaymentAttemptRepository(CreateLinklyRefundAttempt(draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient();
        var service = CreateLinklyServiceWithVoucher(attempts, orders, new FakeLinklyBackendTerminalClient(), vouchers);

        var result = await ResolveLinklyRefundAsync(service, draft);

        Assert.True(result.Succeeded);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        var call = Assert.Single(vouchers.IssueCalls);
        Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey);
        Assert.Equal(4m, call.Amount);
        Assert.Equal(draft.OrderGuid.ToString("D"), call.OrderReference);
        AssertIssuedRefundVoucher(result.RecoveryResult!.Order!, "VOUCHER_REFUND:RV-0001");
        AssertIssuedRefundVoucher(orders.Stored!, "VOUCHER_REFUND:RV-0001");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveRefundAsync_linkly_confirm_refunded_voucher_issue_failure_keeps_placeholder_and_completes(
        bool throwOnIssue)
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-RV-FAIL");
        var attempts = new FakeCardPaymentAttemptRepository(CreateLinklyRefundAttempt(draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailIssue = true, ThrowOnFailure = throwOnIssue };
        var service = CreateLinklyServiceWithVoucher(attempts, orders, new FakeLinklyBackendTerminalClient(), vouchers);

        var result = await ResolveLinklyRefundAsync(service, draft);

        Assert.True(result.Succeeded);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
        Assert.False(result.RecoveryResult!.HasPostCommitWarning);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        Assert.Single(vouchers.IssueCalls);
        Assert.Equal(0, orders.UpdateReferenceCount);
        AssertPendingRefundVoucher(result.RecoveryResult.Order!);
        AssertPendingRefundVoucher(orders.Stored!);
    }

    [Fact]
    public async Task ResolveRefundAsync_linkly_confirm_refunded_repeated_recovery_reuses_same_voucher_key()
    {
        // 同一草稿被恢复两次（例如首次恢复在 attempt 收尾前崩溃、本地订单丢失后重放）：两次都必须使用草稿冻结的幂等键，
        // 服务端按幂等键只开一张券。
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-RV-REPEAT");
        var vouchers = new RecordingRefundVoucherClient();

        for (var run = 0; run < 2; run++)
        {
            var attempts = new FakeCardPaymentAttemptRepository(CreateLinklyRefundAttempt(draft));
            var orders = new VoucherTrackingOrderRepository();
            var service = CreateLinklyServiceWithVoucher(attempts, orders, new FakeLinklyBackendTerminalClient(), vouchers);

            var result = await ResolveLinklyRefundAsync(service, draft);

            Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
            AssertIssuedRefundVoucher(result.RecoveryResult!.Order!, "VOUCHER_REFUND:RV-0001");
        }

        Assert.Equal(2, vouchers.IssueCalls.Count);
        Assert.All(vouchers.IssueCalls, call => Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey));
        Assert.Equal(1, vouchers.DistinctVoucherCount);
    }

    [Fact]
    public async Task ResolveRefundAsync_linkly_confirm_refunded_partial_voucher_issue_returns_order_matching_local_store()
    {
        // 两张退款券：第一张签发成功并已回写，第二张失败。结果里的订单必须与本地库一致（第一张真实、第二张占位）。
        var draft = CreateRefundDraft("ANZ:ORIGINAL-RV-PARTIAL") with
        {
            CurrentTenders =
            [
                new PaymentTender(PaymentMethodKind.Voucher, -2m, PendingRefundVoucherReference, IdempotencyKey: "rv-key-first"),
                new PaymentTender(PaymentMethodKind.Voucher, -2m, PendingRefundVoucherReference, IdempotencyKey: "rv-key-second")
            ],
            CardAmount = 6m
        };
        var attempts = new FakeCardPaymentAttemptRepository(CreateLinklyRefundAttempt(draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailingKeys = { "rv-key-second" } };
        var service = CreateLinklyServiceWithVoucher(attempts, orders, new FakeLinklyBackendTerminalClient(), vouchers);

        var result = await ResolveLinklyRefundAsync(service, draft);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(["rv-key-first", "rv-key-second"], vouchers.IssueCalls.Select(call => call.IdempotencyKey));
        var references = result.RecoveryResult!.Order!.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Voucher)
            .Select(payment => payment.Reference)
            .ToArray();
        Assert.Equal(["VOUCHER_REFUND:RV-0001", PendingRefundVoucherReference], references);
        Assert.Equal(
            references,
            orders.Stored!.Payments.Where(payment => payment.Method == PaymentMethodKind.Voucher).Select(payment => payment.Reference));
    }

    [Fact]
    public async Task RecoverLatestAsync_cloud_approved_refund_draft_issues_pending_refund_voucher()
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-CLOUD-RV");
        var attempts = new FakeCardPaymentAttemptRepository(CreateAttempt("SESSION-RV", "TXN-RV", draft: draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient();
        var service = CreateLinklyServiceWithVoucher(attempts, orders, CreateApprovedCloudBackend("SESSION-RV", "TXN-RV"), vouchers);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        var call = Assert.Single(vouchers.IssueCalls);
        Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey);
        Assert.Equal(4m, call.Amount);
        AssertIssuedRefundVoucher(result.Order!, "VOUCHER_REFUND:RV-0001");
        AssertIssuedRefundVoucher(orders.Stored!, "VOUCHER_REFUND:RV-0001");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverLatestAsync_cloud_approved_refund_voucher_issue_failure_keeps_placeholder_and_completes(
        bool throwOnIssue)
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-CLOUD-RV-FAIL");
        var attempts = new FakeCardPaymentAttemptRepository(CreateAttempt("SESSION-RV-FAIL", "TXN-RV-FAIL", draft: draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailIssue = true, ThrowOnFailure = throwOnIssue };
        var backend = CreateApprovedCloudBackend("SESSION-RV-FAIL", "TXN-RV-FAIL");
        var service = CreateLinklyServiceWithVoucher(attempts, orders, backend, vouchers);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.False(result.HasPostCommitWarning);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal(1, orders.SaveCount);
        Assert.Single(vouchers.IssueCalls);
        Assert.Equal(0, orders.UpdateReferenceCount);
        AssertPendingRefundVoucher(result.Order!);
        AssertPendingRefundVoucher(orders.Stored!);
    }

    [Fact]
    public async Task RecoverLatestAsync_cloud_approved_repeated_recovery_on_saved_order_reuses_key_and_skips_issued_voucher()
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-CLOUD-RV-REPEAT");
        var attempt = CreateAttempt("SESSION-RV-REPEAT", "TXN-RV-REPEAT", draft: draft);
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailIssue = true };

        // 第一次：订单落库，签券失败保留占位。
        var first = await CreateLinklyServiceWithVoucher(
                new FakeCardPaymentAttemptRepository(attempt),
                orders,
                CreateApprovedCloudBackend("SESSION-RV-REPEAT", "TXN-RV-REPEAT"),
                vouchers)
            .RecoverLatestAsync(new PosCartService(), Session);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, first.Outcome);
        AssertPendingRefundVoucher(orders.Stored!);

        // 第二次：同一 attempt 再次恢复命中已保存订单，用同一幂等键补签。
        vouchers.FailIssue = false;
        var second = await CreateLinklyServiceWithVoucher(
                new FakeCardPaymentAttemptRepository(attempt),
                orders,
                CreateApprovedCloudBackend("SESSION-RV-REPEAT", "TXN-RV-REPEAT"),
                vouchers)
            .RecoverLatestAsync(new PosCartService(), Session);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, second.Outcome);
        AssertIssuedRefundVoucher(second.Order!, "VOUCHER_REFUND:RV-0001");
        AssertIssuedRefundVoucher(orders.Stored!, "VOUCHER_REFUND:RV-0001");

        // 第三次：已有真实券号，不再签券。
        var third = await CreateLinklyServiceWithVoucher(
                new FakeCardPaymentAttemptRepository(attempt),
                orders,
                CreateApprovedCloudBackend("SESSION-RV-REPEAT", "TXN-RV-REPEAT"),
                vouchers)
            .RecoverLatestAsync(new PosCartService(), Session);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, third.Outcome);
        AssertIssuedRefundVoucher(third.Order!, "VOUCHER_REFUND:RV-0001");

        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(2, vouchers.IssueCalls.Count);
        Assert.All(vouchers.IssueCalls, call => Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey));
        Assert.Equal(1, orders.UpdateReferenceCount);
    }

    [Fact]
    public async Task RecoverLatestAsync_local_ip_approved_refund_draft_issues_pending_refund_voucher()
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-LOCAL-RV");
        var attempts = new FakeCardPaymentAttemptRepository(CreateLocalIpRefundDraftAttempt(draft, "LOCAL-RV-001"));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient();
        var service = CreateLinklyServiceWithVoucher(
            attempts,
            orders,
            new FakeLinklyBackendTerminalClient(),
            vouchers,
            LinklyConnectionMode.LocalIp,
            CreateApprovedLocalTerminal("LOCAL-RV-001"));

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        var call = Assert.Single(vouchers.IssueCalls);
        Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey);
        Assert.Equal(4m, call.Amount);
        AssertIssuedRefundVoucher(result.Order!, "VOUCHER_REFUND:RV-0001");
        AssertIssuedRefundVoucher(orders.Stored!, "VOUCHER_REFUND:RV-0001");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverLatestAsync_local_ip_approved_refund_voucher_issue_failure_keeps_placeholder_and_completes(
        bool throwOnIssue)
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-LOCAL-RV-FAIL");
        var attempts = new FakeCardPaymentAttemptRepository(CreateLocalIpRefundDraftAttempt(draft, "LOCAL-RV-002"));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailIssue = true, ThrowOnFailure = throwOnIssue };
        var service = CreateLinklyServiceWithVoucher(
            attempts,
            orders,
            new FakeLinklyBackendTerminalClient(),
            vouchers,
            LinklyConnectionMode.LocalIp,
            CreateApprovedLocalTerminal("LOCAL-RV-002"));

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.False(result.HasPostCommitWarning);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        Assert.Single(vouchers.IssueCalls);
        Assert.Equal(0, orders.UpdateReferenceCount);
        AssertPendingRefundVoucher(result.Order!);
        AssertPendingRefundVoucher(orders.Stored!);
    }

    [Fact]
    public async Task RecoverLatestAsync_local_ip_approved_repeated_recovery_on_saved_order_reuses_key_and_skips_issued_voucher()
    {
        var draft = CreateRefundDraftWithPendingVoucher("ANZ:ORIGINAL-LOCAL-RV-REPEAT");
        var attempt = CreateLocalIpRefundDraftAttempt(draft, "LOCAL-RV-003");
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailIssue = true };

        async Task<CardPaymentRecoveryResult> RecoverOnceAsync() =>
            await CreateLinklyServiceWithVoucher(
                    new FakeCardPaymentAttemptRepository(attempt),
                    orders,
                    new FakeLinklyBackendTerminalClient(),
                    vouchers,
                    LinklyConnectionMode.LocalIp,
                    CreateApprovedLocalTerminal("LOCAL-RV-003"))
                .RecoverLatestAsync(new PosCartService(), Session);

        var first = await RecoverOnceAsync();
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, first.Outcome);
        AssertPendingRefundVoucher(orders.Stored!);

        vouchers.FailIssue = false;
        var second = await RecoverOnceAsync();
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, second.Outcome);
        AssertIssuedRefundVoucher(second.Order!, "VOUCHER_REFUND:RV-0001");

        var third = await RecoverOnceAsync();
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, third.Outcome);
        AssertIssuedRefundVoucher(third.Order!, "VOUCHER_REFUND:RV-0001");

        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(2, vouchers.IssueCalls.Count);
        Assert.All(vouchers.IssueCalls, call => Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey));
        Assert.Equal(1, orders.UpdateReferenceCount);
    }

    [Fact]
    public async Task ResolveRefundAsync_square_confirm_refunded_issues_pending_refund_voucher_with_draft_key()
    {
        var draft = CreateRefundDraftWithPendingVoucher("SQ:ORIGINAL-RV");
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt(draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient();
        var service = CreateSquareServiceWithVoucher(attempts, orders, vouchers);

        var result = await ResolveSquareRefundAsync(service, draft);

        Assert.True(result.Succeeded);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        var call = Assert.Single(vouchers.IssueCalls);
        Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey);
        Assert.Equal(4m, call.Amount);
        Assert.Equal(draft.OrderGuid.ToString("D"), call.OrderReference);
        AssertIssuedRefundVoucher(result.RecoveryResult!.Order!, "VOUCHER_REFUND:RV-0001");
        AssertIssuedRefundVoucher(orders.Stored!, "VOUCHER_REFUND:RV-0001");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveRefundAsync_square_confirm_refunded_voucher_issue_failure_keeps_placeholder_and_completes(
        bool throwOnIssue)
    {
        var draft = CreateRefundDraftWithPendingVoucher("SQ:ORIGINAL-RV-FAIL");
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt(draft));
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient { FailIssue = true, ThrowOnFailure = throwOnIssue };
        var service = CreateSquareServiceWithVoucher(attempts, orders, vouchers);

        var result = await ResolveSquareRefundAsync(service, draft);

        Assert.True(result.Succeeded);
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
        Assert.False(result.RecoveryResult!.HasPostCommitWarning);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, orders.SaveCount);
        Assert.Single(vouchers.IssueCalls);
        Assert.Equal(0, orders.UpdateReferenceCount);
        AssertPendingRefundVoucher(result.RecoveryResult.Order!);
        AssertPendingRefundVoucher(orders.Stored!);
    }

    [Fact]
    public async Task ResolveRefundAsync_square_confirm_refunded_repeated_recovery_reuses_same_voucher_key()
    {
        var draft = CreateRefundDraftWithPendingVoucher("SQ:ORIGINAL-RV-REPEAT");
        var vouchers = new RecordingRefundVoucherClient();

        for (var run = 0; run < 2; run++)
        {
            var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt(draft));
            var orders = new VoucherTrackingOrderRepository();
            var service = CreateSquareServiceWithVoucher(attempts, orders, vouchers);

            var result = await ResolveSquareRefundAsync(service, draft);

            Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.RecoveryResult?.Outcome);
            AssertIssuedRefundVoucher(result.RecoveryResult!.Order!, "VOUCHER_REFUND:RV-0001");
        }

        Assert.Equal(2, vouchers.IssueCalls.Count);
        Assert.All(vouchers.IssueCalls, call => Assert.Equal(DraftRefundVoucherKey, call.IdempotencyKey));
        Assert.Equal(1, vouchers.DistinctVoucherCount);
    }

    [Fact]
    public async Task RecoverAttemptAsync_square_verified_sale_draft_with_negative_voucher_is_rejected_without_issuing()
    {
        // Square 已核验付款落单分支（CompleteVerifiedAttemptAsync）只服务销售 attempt：销售草稿不允许负向 tender，
        // 所以该分支不可能出现待签退款券；这里钉住这一前提，防止之后放宽校验却漏掉补签。
        var draft = CreateDraft(
            currentTenders: [new PaymentTender(PaymentMethodKind.Voucher, -4m, PendingRefundVoucherReference, IdempotencyKey: DraftRefundVoucherKey)]);
        var attempt = CreateSquareAttempt(
            LocalSquarePaymentAttemptStatus.PaymentVerified,
            checkoutId: null,
            paymentId: "PAYMENT-SALE-NEG-VOUCHER",
            paymentStatus: "COMPLETED") with
        {
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted,
            OrderDraftJson = JsonSerializer.Serialize(draft, JsonOptions)
        };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new VoucherTrackingOrderRepository();
        var vouchers = new RecordingRefundVoucherClient();
        var service = CreateSquareServiceWithVoucher(attempts, orders, vouchers);

        var result = await service.RecoverAttemptAsync(attempt.AttemptGuid, new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
        Assert.Empty(vouchers.IssueCalls);
    }

    // 退货 10 元：先在付款页加了 4 元「退款券」（离线占位），剩余 6 元走卡退款。
    private static CardPaymentOrderDraft CreateRefundDraftWithPendingVoucher(string originalReference) =>
        CreateRefundDraft(originalReference) with
        {
            CurrentTenders =
            [
                new PaymentTender(
                    PaymentMethodKind.Voucher,
                    -4m,
                    PendingRefundVoucherReference,
                    IdempotencyKey: DraftRefundVoucherKey)
            ],
            CardAmount = 6m
        };

    private static LocalCardPaymentAttempt CreateLinklyRefundAttempt(CardPaymentOrderDraft draft) =>
        CreateAttempt(
            sessionId: "SESSION-REFUND-RV",
            txnRef: "TXN-REFUND-RV",
            status: LocalCardPaymentAttemptStatus.Recovering,
            draft: draft) with
        {
            OperationKind = "Refund",
            OperationGuid = draft.OrderGuid,
            TxnType = "R"
        };

    private static LocalCardPaymentAttempt CreateLocalIpRefundDraftAttempt(CardPaymentOrderDraft draft, string txnRef) =>
        CreateAttempt(
            sessionId: null,
            txnRef: txnRef,
            connectionMode: LinklyConnectionMode.LocalIp,
            draft: draft);

    private static LocalSquarePaymentAttempt CreateSquareRefundAttempt(CardPaymentOrderDraft draft) =>
        CreateSquareAttempt(
            LocalSquarePaymentAttemptStatus.Unknown,
            checkoutId: "CHECKOUT-REFUND-RV",
            paymentId: null) with
        {
            OperationKind = "Refund",
            OperationGuid = draft.OrderGuid,
            Amount = Math.Abs(draft.CardAmount),
            AmountCents = (long)(Math.Abs(draft.CardAmount) * 100m),
            OrderDraftJson = JsonSerializer.Serialize(draft, JsonOptions)
        };

    private static FakeLinklyBackendTerminalClient CreateApprovedCloudBackend(string sessionId, string txnRef) =>
        new()
        {
            Status = WithVerifiedCardTransaction(
                CreateStatus(
                    "Completed",
                    sessionId: sessionId,
                    txnRef: txnRef,
                    responseCode: "00",
                    responseText: "APPROVED",
                    transactionSuccess: true),
                6m)
        };

    private static FakeLinklyTerminalClient CreateApprovedLocalTerminal(string txnRef) =>
        new(new PaymentAuthorizationResult(
            true,
            $"ANZ:{txnRef}",
            "ANZ Linkly",
            6m,
            [CreateLocalCardTransaction(txnRef, "00", "APPROVED", 6m)],
            "ANZ",
            "Sandbox",
            LinklyConnectionMode.LocalIp.ToString(),
            "P",
            null,
            txnRef,
            "00",
            "APPROVED"));

    private static Task<CardRefundSupervisorResolutionResult> ResolveLinklyRefundAsync(
        CardPaymentRecoveryService service,
        CardPaymentOrderDraft draft) =>
        service.ResolveRefundAsync(
            new CardRefundSupervisorResolution(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                CardProcessorKind.Linkly,
                CardRefundSupervisorDecision.ConfirmRefunded,
                Reason: "Matched bank settlement",
                RefundReference: $"LINKLY-REFUND-{draft.OriginalReference}"),
            new PosCartService(),
            Session);

    private static Task<CardRefundSupervisorResolutionResult> ResolveSquareRefundAsync(
        SquarePaymentRecoveryService service,
        CardPaymentOrderDraft draft) =>
        service.ResolveRefundAsync(
            new CardRefundSupervisorResolution(
                Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                CardProcessorKind.Square,
                CardRefundSupervisorDecision.ConfirmRefunded,
                Reason: "Matched Square settlement",
                RefundReference: $"SQ-REFUND-{draft.OriginalReference}"),
            new PosCartService(),
            Session);

    private static CardPaymentRecoveryService CreateLinklyServiceWithVoucher(
        FakeCardPaymentAttemptRepository attempts,
        VoucherTrackingOrderRepository orders,
        FakeLinklyBackendTerminalClient backend,
        RecordingRefundVoucherClient vouchers,
        LinklyConnectionMode connectionMode = LinklyConnectionMode.CloudBackendAsync,
        FakeLinklyTerminalClient? linklyTerminalClient = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalCardPaymentAttemptRepository>(attempts);
        services.AddSingleton<ICardTerminalSettingsProvider>(new FakeCardTerminalSettingsProvider(connectionMode));
        services.AddSingleton<ILinklyBackendTerminalClient>(backend);
        services.AddSingleton(new CashCheckoutService());
        services.AddSingleton<ILocalOrderRepository>(orders);
        services.AddSingleton<ISyncQueueRepository>(new FakeSyncQueueRepository());
        services.AddSingleton<IVoucherTenderClient>(vouchers);
        if (linklyTerminalClient is not null)
        {
            services.AddSingleton<ILinklyTerminalClient>(linklyTerminalClient);
        }

        services.AddSingleton<CardPaymentRecoveryService>();
        return services.BuildServiceProvider().GetRequiredService<CardPaymentRecoveryService>();
    }

    private static SquarePaymentRecoveryService CreateSquareServiceWithVoucher(
        FakeSquarePaymentAttemptRepository attempts,
        VoucherTrackingOrderRepository orders,
        RecordingRefundVoucherClient vouchers)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalSquarePaymentAttemptRepository>(attempts);
        services.AddSingleton<ICardTerminalSettingsProvider>(new FakeSquareCardTerminalSettingsProvider());
        services.AddSingleton<ISquareTerminalPaymentClient>(new FakeSquareTerminalPaymentClient());
        services.AddSingleton(new CashCheckoutService());
        services.AddSingleton<ILocalOrderRepository>(orders);
        services.AddSingleton<IVoucherTenderClient>(vouchers);
        services.AddSingleton<SquarePaymentRecoveryService>();
        return services.BuildServiceProvider().GetRequiredService<SquarePaymentRecoveryService>();
    }

    private static void AssertPendingRefundVoucher(LocalOrder order)
    {
        var voucher = Assert.Single(order.Payments, payment => payment.Method == PaymentMethodKind.Voucher);
        Assert.Equal(-4m, voucher.Amount);
        Assert.Equal(PendingRefundVoucherReference, voucher.Reference);
        Assert.Equal(DraftRefundVoucherKey, voucher.IdempotencyKey);
        Assert.Single(order.Payments, payment => payment.Method == PaymentMethodKind.Card && payment.Amount == -6m);
    }

    private static void AssertIssuedRefundVoucher(LocalOrder order, string expectedReference)
    {
        var voucher = Assert.Single(order.Payments, payment => payment.Method == PaymentMethodKind.Voucher);
        Assert.Equal(-4m, voucher.Amount);
        Assert.Equal(expectedReference, voucher.Reference);
        Assert.Equal(DraftRefundVoucherKey, voucher.IdempotencyKey);
        Assert.Single(order.Payments, payment => payment.Method == PaymentMethodKind.Card && payment.Amount == -6m);
    }

    /// <summary>按幂等键去重的退款券服务端替身：同一幂等键永远返回同一张券。</summary>
    private sealed class RecordingRefundVoucherClient : IVoucherTenderClient
    {
        private readonly Dictionary<string, string> _issuedByKey = new(StringComparer.Ordinal);

        public List<(decimal Amount, string OrderReference, string IdempotencyKey, string CashierId)> IssueCalls { get; } = [];

        public bool FailIssue { get; set; }

        public bool ThrowOnFailure { get; init; }

        public HashSet<string> FailingKeys { get; } = new(StringComparer.Ordinal);

        public int DistinctVoucherCount => _issuedByKey.Count;

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueCalls.Add((amount, orderReference, idempotencyKey, session.CashierId));
            if (FailIssue || FailingKeys.Contains(idempotencyKey))
            {
                if (ThrowOnFailure)
                {
                    throw new HttpRequestException("voucher service unreachable");
                }

                return Task.FromResult(new PaymentAuthorizationResult(false, null, "Voucher refund issuing failed."));
            }

            if (!_issuedByKey.TryGetValue(idempotencyKey, out var code))
            {
                code = $"RV-{_issuedByKey.Count + 1:0000}";
                _issuedByKey[idempotencyKey] = code;
            }

            return Task.FromResult(new PaymentAuthorizationResult(true, $"VOUCHER_REFUND:{code}", code, amount));
        }

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>保存订单并按 PaymentGuid 回写引用的本地订单库替身。</summary>
    private sealed class VoucherTrackingOrderRepository : ILocalOrderRepository
    {
        private readonly object _gate = new();

        public LocalOrder? Stored { get; private set; }

        public int SaveCount { get; private set; }

        public int UpdateReferenceCount { get; private set; }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                SaveCount++;
                Stored = order;
            }

            return Task.CompletedTask;
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                UpdateReferenceCount++;
                if (Stored is not null)
                {
                    Stored = Stored with
                    {
                        Payments = Stored.Payments
                            .Select(payment => payment.PaymentGuid == paymentGuid
                                ? payment with { Reference = reference }
                                : payment)
                            .ToArray()
                    };
                }
            }

            return Task.CompletedTask;
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(Stored is not null && Stored.OrderGuid == orderGuid ? Stored : null);
            }
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
    }
}
