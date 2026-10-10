using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// Square 批准恢复完成路径：必须用 payment draft 冻结的 durable claim binding 解析取单来源；
/// 当前 UI 购物车内容绝不参与来源匹配。
/// </summary>
public sealed class SquarePaymentRecoveryServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PosSessionState Session = new("HB POS", "S001", "Main Branch", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task RecoverLatestAsync_square_verified_binds_matching_active_claim_from_frozen_draft_snapshot()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var holdGuid = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            holdGuid,
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-sq",
            CanonicalForDraftCart(),
            "2026-07-28T00:00:00.000Z")));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimId, "prepare-sq", "activate-sq", serverRevision: null, "2026-07-28T00:00:01.000Z"));

        var draft = CreateDraft();
        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareAttempt(LocalSquarePaymentAttemptStatus.CheckoutCreated, "CHECKOUT-001") with
            {
                OrderDraftJson = JsonSerializer.Serialize(
                    draft with
                    {
                        CartSnapshot = draft.CartSnapshot with { SharedHeldOrderClaimId = claimId }
                    },
                    JsonOptions)
            });
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, new FakeSquareTerminalPaymentClient(), scope.Repository);

        // 当前 UI 购物车为空：Square 批准恢复必须使用 draft 中冻结的 durable claim binding 解析来源。
        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        var heldCompletion = Assert.Single(orders.HeldSources);
        Assert.Equal(result.Order!.OrderGuid, heldCompletion.Order.OrderGuid);
        Assert.Equal(holdGuid, heldCompletion.Context.HoldGuid);
        Assert.Equal(claimId, heldCompletion.Context.ClaimId);
        Assert.Equal(SharedHeldOrderClaimSource.OfflineOrigin, heldCompletion.Context.Source);
        Assert.Equal("prepare-sq", heldCompletion.Context.PrepareIdempotencyKey);
        Assert.Equal("activate-sq", heldCompletion.Context.ActivateIdempotencyKey);
        Assert.Equal(1, orders.SaveCount);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_verified_with_non_matching_claim_does_not_bind_held_source()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            Guid.NewGuid(),
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-sq-nomatch",
            CanonicalForDraftCart(quantity: 2m),
            "2026-07-28T00:00:00.000Z")));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimId, "prepare-sq-nomatch", "activate-sq-nomatch", serverRevision: null, "2026-07-28T00:00:01.000Z"));

        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareAttempt(LocalSquarePaymentAttemptStatus.CheckoutCreated, "CHECKOUT-NOMATCH"));
        var orders = new FakeLocalOrderRepository();
        var service = CreateService(attempts, orders, new FakeSquareTerminalPaymentClient(), scope.Repository);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, orders.SaveCount);
        Assert.Empty(orders.HeldSources);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_verified_with_existing_order_skips_bound_claim_resolution()
    {
        await using var scope = await CreateRepositoryScopeAsync();
        var holdGuid = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        Assert.True(await scope.Repository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
            claimId,
            holdGuid,
            "S001",
            "POS-01",
            SharedHeldOrderClaimSource.OfflineOrigin,
            "prepare-sq-existing",
            CanonicalForDraftCart(),
            "2026-07-28T00:00:00.000Z")));
        Assert.True(await scope.Repository.TryActivateClaimAsync(
            claimId,
            "prepare-sq-existing",
            "activate-sq-existing",
            serverRevision: null,
            "2026-07-28T00:00:01.000Z"));
        // 订单已保存、attempt 未收尾：claim 已绑定并完成（与 LocalOrder 同事务）。
        Assert.True(await scope.Repository.TryBindOrderAsync(
            claimId,
            "activate-sq-existing",
            CreateDraftOrderGuid().ToString("D"),
            "2026-07-28T00:00:02.000Z"));
        Assert.True(await scope.Repository.TryCompleteClaimAsync(
            claimId,
            "activate-sq-existing",
            "release-sq-existing",
            "2026-07-28T00:00:03.000Z"));

        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareAttempt(LocalSquarePaymentAttemptStatus.CheckoutCreated, "CHECKOUT-EXISTING"));
        var orders = new FakeLocalOrderRepository(CreateExistingOrder());
        var service = CreateService(attempts, orders, new FakeSquareTerminalPaymentClient(), scope.Repository);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        // 既有订单幂等收尾：不再解析已 Completed/bound 的 held claim，直接完成 attempt，
        // 不重复保存订单，也绝不把来源静默降级为普通订单。
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(CreateDraftOrderGuid(), result.Order!.OrderGuid);
        Assert.Equal(0, orders.SaveCount);
        Assert.Empty(orders.HeldSources);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_pending_refund_completes_the_return_and_keeps_settlement_pending()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt());
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "PENDING",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        // Square 官方定义 PENDING 为已受理、正在退回原卡：与 COMPLETED 一样完成退货单，
        // 退款状态保留 PENDING，交给结算跟踪继续查询；绝不重新发起退款。
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal("PENDING", attempts.PaymentStatus);
        Assert.Equal(-10m, Assert.Single(result.Order!.Payments).Amount);
    }

    private static LocalSquarePaymentAttempt CreateUnboundSquareRefundAttempt() =>
        CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentId = null,
            PaymentStatus = null
        };

    private static SquareRefundStatusResult CreateDiscoverableRefund(
        string refundId,
        LocalSquarePaymentAttempt attempt,
        TimeSpan createdOffset,
        long amountCents = 1000) =>
        new(refundId, "PENDING", "PAYMENT-001", amountCents, "AUD", CreatedAt: attempt.CreatedAt + createdOffset);

    [Fact]
    public async Task RecoverLatestAsync_unbound_refund_is_discovered_from_original_payment_and_completes_the_return()
    {
        var attempt = CreateUnboundSquareRefundAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Payment = new SquarePaymentStatusResult("PAYMENT-001", "COMPLETED", 1000, "AUD", RefundIds: ["REFUND-001"])
        };
        terminal.RefundsById["REFUND-001"] = CreateDiscoverableRefund("REFUND-001", attempt, TimeSpan.FromSeconds(20));
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        // 回执丢失但 Square 已受理：按原付款的 refund_ids 找回并绑定，再沿用原有自动恢复，不会重复退款。
        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal("PENDING", attempts.PaymentStatus);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("bound-elsewhere")]
    [InlineData("amount-mismatch")]
    [InlineData("created-outside-window")]
    [InlineData("created-unknown")]
    public async Task RecoverLatestAsync_unbound_refund_stays_for_supervisor_when_discovery_is_not_conclusive(string scenario)
    {
        var attempt = CreateUnboundSquareRefundAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt)
        {
            RefundIdBoundToOtherAttempt = scenario == "bound-elsewhere"
        };
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Payment = new SquarePaymentStatusResult(
                "PAYMENT-001", "COMPLETED", 1000, "AUD",
                RefundIds: scenario == "ambiguous" ? ["REFUND-001", "REFUND-002"] : ["REFUND-001"])
        };
        terminal.RefundsById["REFUND-001"] = scenario switch
        {
            "amount-mismatch" => CreateDiscoverableRefund("REFUND-001", attempt, TimeSpan.FromSeconds(20), amountCents: 500),
            "created-outside-window" => CreateDiscoverableRefund("REFUND-001", attempt, TimeSpan.FromHours(2)),
            "created-unknown" => CreateDiscoverableRefund("REFUND-001", attempt, TimeSpan.Zero) with { CreatedAt = null },
            _ => CreateDiscoverableRefund("REFUND-001", attempt, TimeSpan.FromSeconds(20))
        };
        terminal.RefundsById["REFUND-002"] = CreateDiscoverableRefund("REFUND-002", attempt, TimeSpan.FromSeconds(30));
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        // 任何不确定都不能自动认领退款：保持“结果未知”，交给主管对账。
        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, attempts.MarkOrderCompletedCount);
        Assert.Null(attempts.PaymentStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_completed_refund_creates_return_without_second_refund()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt());
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "COMPLETED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(-10m, Assert.Single(result.Order!.Payments).Amount);
    }

    [Fact]
    public async Task RecoverLatestAsync_local_completed_refund_replays_without_square_lookup_even_when_remote_failed()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareRefundAttempt() with
            {
                PaymentStatus = " completed ",
                RecoveryPhase = CardRecoveryPhases.None,
                RecoveryTargetStatus = null
            });
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(0, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
        Assert.Equal(1, orders.SaveCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, saved.Status);
        Assert.Equal(" completed ", saved.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
        Assert.Null(saved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_completed_refund_replaces_continue_waiting_decision()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareRefundAttempt() with
            {
                ResponseCode = CardRefundSupervisorResolutionCodes.ContinueWaiting,
                ResponseText = "Supervisor chose to continue waiting."
            });
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "COMPLETED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.MarkOrderCompletedCount);
        Assert.Equal(1, orders.SaveCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, saved.Status);
        Assert.Null(saved.ResponseCode);
        Assert.Equal("Square refund status confirmed by lookup.", saved.ResponseText);
        Assert.Equal("COMPLETED", saved.PaymentStatus);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("REJECTED")]
    public async Task RecoverLatestAsync_square_failed_or_rejected_refund_persists_finalize_pending_then_restores_retry(
        string refundStatus)
    {
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt());
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                refundStatus,
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var cart = new PosCartService();
        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.True(result.RequiresAlternativeRefundMethod);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.PersistRefundFailureForFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, attempts.Status);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(refundStatus, saved.PaymentStatus);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, saved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
        Assert.Equal("REFUND-001", saved.PaymentId);
        Assert.Single(cart.CreateSnapshot().Lines);
        Assert.Equal(saved.AttemptGuid, cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(0, orders.SaveCount);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_failed_refund_with_non_empty_cart_keeps_finalize_pending()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt());
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();
        cart.RestoreSnapshot(CreateDraft().CartSnapshot);

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, attempts.Status);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
        Assert.False(cart.IsEmpty);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_failed_refund_with_empty_draft_keeps_finalize_pending()
    {
        var draft = CreateRefundDraft("SQ:PAYMENT-001");
        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareRefundAttempt() with
            {
                OrderDraftJson = JsonSerializer.Serialize(
                    draft with { CartSnapshot = draft.CartSnapshot with { Lines = [] } },
                    JsonOptions)
            });
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);

        var cart = new PosCartService();
        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, saved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_failed_refund_cas_failure_replays_completed_winner_without_overwriting_it()
    {
        var initial = CreateSquareRefundAttempt();
        var winner = initial with
        {
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            PaymentStatus = " COMPLETED ",
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null,
            UpdatedAt = initial.UpdatedAt.AddMinutes(1)
        };
        var attempts = new FakeSquarePaymentAttemptRepository(initial)
        {
            PersistRefundFailureForFinalizationResult = false,
            RefundFailureCasWinner = winner
        };
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, orders.SaveCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(initial.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, saved.Status);
        Assert.Equal(" COMPLETED ", saved.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
        Assert.Equal("REFUND-001", saved.PaymentId);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_failed_refund_cas_failure_keeps_pending_winner_without_second_lookup()
    {
        var initial = CreateSquareRefundAttempt();
        var winner = initial with
        {
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            PaymentStatus = "PENDING",
            UpdatedAt = initial.UpdatedAt.AddMinutes(1)
        };
        var attempts = new FakeSquarePaymentAttemptRepository(initial)
        {
            PersistRefundFailureForFinalizationResult = false,
            RefundFailureCasWinner = winner
        };
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Checking, result.Outcome);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.PersistRefundFailureForFinalizationCount);
        Assert.Equal(0, orders.SaveCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(initial.AttemptGuid));
        Assert.Equal(winner.Status, saved.Status);
        Assert.Equal("PENDING", saved.PaymentStatus);
        Assert.Equal(winner.UpdatedAt, saved.UpdatedAt);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("ReJeCtEd")]
    public async Task RecoverLatestAsync_square_refund_failure_cas_winner_requires_alternative_refund_method(
        string winnerPaymentStatus)
    {
        var initial = CreateSquareRefundAttempt();
        var winner = initial with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentStatus = winnerPaymentStatus,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned,
            UpdatedAt = initial.UpdatedAt.AddMinutes(1)
        };
        var attempts = new FakeSquarePaymentAttemptRepository(initial)
        {
            PersistRefundFailureForFinalizationResult = false,
            RefundFailureCasWinner = winner
        };
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.True(result.RequiresAlternativeRefundMethod);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(1, attempts.PersistRefundFailureForFinalizationCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, attempts.Status);
        Assert.Equal(winner.AttemptGuid, cart.RecoveryOwnerAttemptGuid);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(initial.AttemptGuid));
        Assert.Equal(winnerPaymentStatus, saved.PaymentStatus);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, saved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_failed_refund_restarts_after_publication_without_second_square_lookup()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt());
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var publishedCart = new PosCartService();

        var first = await service.RecoverLatestAsync(publishedCart, Session);
        var restartedCart = new PosCartService();
        var restartedService = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var second = await restartedService.RecoverLatestAsync(restartedCart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, first.Outcome);
        Assert.True(first.RequiresAlternativeRefundMethod);
        Assert.Equal(CreateSquareRefundAttempt().AttemptGuid, publishedCart.RecoveryOwnerAttemptGuid);
        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, second.Outcome);
        Assert.True(second.RequiresAlternativeRefundMethod);
        Assert.Equal(1, terminal.GetRefundCallCount);
        Assert.Equal(CreateSquareRefundAttempt().AttemptGuid, restartedCart.RecoveryOwnerAttemptGuid);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, attempts.Status);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_supervisor_confirmed_not_refunded_keeps_standard_retry_contract()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareRefundAttempt() with
            {
                Status = LocalSquarePaymentAttemptStatus.Pending,
                PaymentId = null,
                PaymentStatus = null,
                SubmissionToken = null,
                ResponseCode = CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                ResponseText = "Supervisor confirmed that no refund was processed."
        });
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.False(result.RequiresAlternativeRefundMethod);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var pendingHandoff = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(CardRecoveryPhases.FinalizePending, pendingHandoff.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pendingHandoff.RecoveryTargetStatus);
        Assert.Equal(pendingHandoff.AttemptGuid, cart.RecoveryOwnerAttemptGuid);

        Assert.True(await service.CompleteDraftHandoffAsync(pendingHandoff.AttemptGuid, cart));

        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, attempts.Status);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
    }

    [Fact]
    public async Task Supervisor_not_refunded_ui_handoff_finalization_failure_keeps_pending_owner_for_retry()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(
            CreateSquareRefundAttempt() with
            {
                Status = LocalSquarePaymentAttemptStatus.Pending,
                PaymentId = null,
                PaymentStatus = null,
                SubmissionToken = null,
                ResponseCode = CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded,
                ResponseText = "Supervisor confirmed that no refund was processed."
            })
        {
            FailRecoveryFinalization = true
        };
        var service = CreateService(
            attempts,
            new FakeLocalOrderRepository(),
            new FakeSquareTerminalPaymentClient());
        var cart = new PosCartService();

        var restored = await service.RecoverLatestAsync(cart, Session);
        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        var attemptGuid = CreateSquareRefundAttempt().AttemptGuid;

        Assert.False(await service.CompleteDraftHandoffAsync(attemptGuid, cart));

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, pending.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
        Assert.Equal(attemptGuid, cart.RecoveryOwnerAttemptGuid);
    }

    [Fact]
    public async Task RecoverLatestAsync_supervisor_not_paid_sale_preserves_pending_owner_and_full_tenders_until_handoff()
    {
        var attempt = CreateSquareSupervisorNotPaidSaleAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var service = CreateService(attempts, new FakeLocalOrderRepository(), new FakeSquareTerminalPaymentClient());
        var cart = new PosCartService();
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid);

        var restored = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        Assert.Equal(attemptKey, restored.DraftHandoffKey);
        Assert.Equal(10m, restored.TenderedAmount);
        Assert.Collection(
            restored.RestoredTenders!,
            tender =>
            {
                Assert.Equal(PaymentMethodKind.Cash, tender.Method);
                Assert.Equal(4m, tender.Amount);
            },
            tender =>
            {
                Assert.Equal(PaymentMethodKind.Voucher, tender.Method);
                Assert.Equal(6m, tender.Amount);
            });

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, pending.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
        Assert.Equal(attemptKey, cart.RecoveryOwnerAttemptKey);

        Assert.True(await service.CompleteDraftHandoffAsync(attempt.AttemptGuid, cart));

        var completed = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, completed.Status);
        Assert.Equal(CardRecoveryPhases.None, completed.RecoveryPhase);
        Assert.Null(completed.RecoveryTargetStatus);
        Assert.Null(cart.RecoveryOwnerAttemptKey);
    }

    [Fact]
    public async Task RecoverLatestAsync_canceled_sale_preserves_pending_owner_and_full_tenders_until_handoff()
    {
        var attempt = CreateSquareCanceledSaleAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Checkout = new SquareCheckoutStatusResult(
                "CHECKOUT-001",
                "CANCELED",
                1000,
                "AUD",
                [],
                "USER_CANCELED")
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();
        var attemptKey = new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid);

        var restored = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        Assert.Equal(attemptKey, restored.DraftHandoffKey);
        Assert.Equal(10m, restored.TenderedAmount);
        Assert.Equal(2, restored.RestoredTenders!.Count);
        Assert.Equal(1, terminal.GetCheckoutCallCount);
        Assert.Equal(attemptKey, cart.RecoveryOwnerAttemptKey);

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, pending.Status);
        Assert.Equal("CANCELED", pending.CheckoutStatus);
        Assert.Equal("USER_CANCELED", pending.CancelReason);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, pending.RecoveryTargetStatus);

        Assert.True(await service.CompleteDraftHandoffAsync(attempt.AttemptGuid, cart));

        var completed = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, completed.Status);
        Assert.Equal(CardRecoveryPhases.None, completed.RecoveryPhase);
        Assert.Null(completed.RecoveryTargetStatus);
        Assert.Null(cart.RecoveryOwnerAttemptKey);
    }

    [Fact]
    public async Task RecoverLatestAsync_supervisor_not_paid_sale_restarts_from_local_pending_without_square_lookup()
    {
        var attempt = CreateSquareSupervisorNotPaidSaleAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var terminal = new FakeSquareTerminalPaymentClient();
        var firstService = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var firstCart = new PosCartService();

        var first = await firstService.RecoverLatestAsync(firstCart, Session);
        var restartedService = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var restartedCart = new PosCartService();
        var second = await restartedService.RecoverLatestAsync(restartedCart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, first.Outcome);
        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, second.Outcome);
        Assert.Equal(first.DraftHandoffKey, second.DraftHandoffKey);
        Assert.Equal(10m, second.TenderedAmount);
        Assert.Equal(0, terminal.GetCheckoutCallCount);
        Assert.Equal(0, terminal.GetPaymentCallCount);
        Assert.Equal(attempt.AttemptGuid, restartedCart.RecoveryOwnerAttemptGuid);

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_canceled_sale_restarts_from_local_pending_without_second_square_lookup()
    {
        var attempt = CreateSquareCanceledSaleAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Checkout = new SquareCheckoutStatusResult(
                "CHECKOUT-001",
                "CANCELED",
                1000,
                "AUD",
                [],
                "USER_CANCELED")
        };
        var firstService = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var firstCart = new PosCartService();

        var first = await firstService.RecoverLatestAsync(firstCart, Session);
        var restartedService = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var restartedCart = new PosCartService();
        var second = await restartedService.RecoverLatestAsync(restartedCart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, first.Outcome);
        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, second.Outcome);
        Assert.Equal(first.DraftHandoffKey, second.DraftHandoffKey);
        Assert.Equal(10m, second.TenderedAmount);
        Assert.Equal(1, terminal.GetCheckoutCallCount);
        Assert.Equal(0, terminal.GetPaymentCallCount);
        Assert.Equal(attempt.AttemptGuid, restartedCart.RecoveryOwnerAttemptGuid);
    }

    [Fact]
    public async Task RecoverLatestAsync_canceled_sale_evidence_without_phase_repairs_before_square_lookup()
    {
        var attempt = CreateSquareCanceledSaleAttempt() with
        {
            CheckoutStatus = "CANCELED",
            CancelReason = "USER_CANCELED",
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null
        };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();

        var restored = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid),
            restored.DraftHandoffKey);
        Assert.Equal(0, terminal.GetCheckoutCallCount);
        Assert.Equal(0, terminal.GetPaymentCallCount);
        Assert.Equal(attempt.AttemptGuid, cart.RecoveryOwnerAttemptGuid);

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal("CANCELED", pending.CheckoutStatus);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, pending.RecoveryTargetStatus);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("REJECTED")]
    public async Task RecoverLatestAsync_local_terminal_refund_failure_repairs_before_square_lookup(
        string paymentStatus)
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Recovering,
            PaymentId = "REFUND-001",
            PaymentStatus = paymentStatus,
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null
        };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();

        var restored = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        // FAILED/REJECTED 的 owner 必须留给替代退款订单落库路径收尾，不能由 UI handoff 提前终态化。
        Assert.Null(restored.DraftHandoffKey);
        Assert.True(restored.RequiresAlternativeRefundMethod);
        Assert.Equal(0, terminal.GetRefundCallCount);
        Assert.Equal(
            new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid),
            cart.RecoveryOwnerAttemptKey);

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, pending.Status);
        Assert.Equal(paymentStatus, pending.PaymentStatus);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_sqlite_failed_refund_intermediate_state_is_recoverable_without_square_lookup(
        bool recoverExplicitAttempt)
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var attempts = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareRefundAttempt() with
            {
                Status = LocalSquarePaymentAttemptStatus.Failed,
                PaymentId = "REFUND-FAILED-INTERMEDIATE-001",
                PaymentStatus = "FAILED",
                ResponseCode = "SQUARE_FAILURE",
                ResponseText = "Square refund reached a terminal failure.",
                RecoveryPhase = CardRecoveryPhases.None,
                RecoveryTargetStatus = null
            };
            await attempts.CreateAsync(attempt);

            var terminal = new FakeSquareTerminalPaymentClient();
            var service = new SquarePaymentRecoveryService(
                attempts,
                new FakeSquareCardTerminalSettingsProvider(),
                terminal,
                new CashCheckoutService(),
                new FakeLocalOrderRepository());
            var cart = new PosCartService();

            var restored = recoverExplicitAttempt
                ? await service.RecoverAttemptAsync(attempt.AttemptGuid, cart, Session)
                : await service.RecoverLatestAsync(cart, Session);

            Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
            Assert.True(restored.RequiresAlternativeRefundMethod);
            Assert.Null(restored.DraftHandoffKey);
            Assert.Equal(0, terminal.GetRefundCallCount);
            Assert.Equal(
                new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid),
                cart.RecoveryOwnerAttemptKey);

            var pending = Assert.IsType<LocalSquarePaymentAttempt>(
                await attempts.GetAttemptAsync(attempt.AttemptGuid));
            Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, pending.Status);
            Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData(ActiveSessionSupervisorResolutionCodes.ConfirmedPaid)]
    [InlineData(ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid)]
    public async Task RecoverAttemptAsync_failed_refund_with_persisted_supervisor_code_does_not_repair_or_call_square(
        string responseCode)
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Failed,
            PaymentId = "REFUND-SUPERVISOR-GUARD-002",
            PaymentStatus = "FAILED",
            ResponseCode = responseCode,
            ResponseText = "A persisted supervisor decision must remain authoritative.",
            RecoveryPhase = CardRecoveryPhases.None,
            RecoveryTargetStatus = null
        };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);

        var result = await service.RecoverAttemptAsync(
            attempt.AttemptGuid,
            new PosCartService(),
            Session);

        Assert.Equal(CardPaymentRecoveryOutcome.None, result.Outcome);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(
            await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Failed, saved.Status);
        Assert.Equal(responseCode, saved.ResponseCode);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
    }

    [Fact]
    public async Task CompleteDraftHandoffAsync_sale_cas_failure_keeps_pending_owner_open()
    {
        var attempt = CreateSquareSupervisorNotPaidSaleAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt)
        {
            FailRecoveryFinalization = true
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), new FakeSquareTerminalPaymentClient());
        var cart = new PosCartService();

        var restored = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        Assert.False(await service.CompleteDraftHandoffAsync(attempt.AttemptGuid, cart));

        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, pending.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
        Assert.Equal(new CardRecoveryAttemptKey(CardProcessorKind.Square, attempt.AttemptGuid), cart.RecoveryOwnerAttemptKey);
    }

    [Fact]
    public async Task CompleteDraftHandoffAsync_sale_read_failure_keeps_pending_owner_open()
    {
        var attempt = CreateSquareSupervisorNotPaidSaleAttempt();
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var service = CreateService(attempts, new FakeLocalOrderRepository(), new FakeSquareTerminalPaymentClient());
        var cart = new PosCartService();

        var restored = await service.RecoverLatestAsync(cart, Session);
        attempts.GetAttemptException = new InvalidOperationException("simulated local read failure");

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, restored.Outcome);
        Assert.False(await service.CompleteDraftHandoffAsync(attempt.AttemptGuid, cart));
        Assert.Equal(attempt.AttemptGuid, cart.RecoveryOwnerAttemptGuid);

        attempts.GetAttemptException = null;
        var pending = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(CardRecoveryPhases.FinalizePending, pending.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, pending.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_failed_refund_keeps_finalize_pending_after_publication()
    {
        var attempts = new FakeSquarePaymentAttemptRepository(CreateSquareRefundAttempt())
        {
            FailRecoveryFinalization = true
        };
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Refund = new SquareRefundStatusResult(
                "REFUND-001",
                "FAILED",
                "PAYMENT-001",
                1000,
                "AUD")
        };
        var service = CreateService(attempts, new FakeLocalOrderRepository(), terminal);
        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.True(result.RequiresAlternativeRefundMethod);
        Assert.False(cart.IsEmpty);
        Assert.Equal(CreateSquareRefundAttempt().AttemptGuid, cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(1, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(CreateSquareRefundAttempt().AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, saved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("REJECTED")]
    public async Task RecoverLatestAsync_finalize_pending_failed_refund_with_matching_existing_cash_order_completes_without_square_lookup(
        string paymentStatus)
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentStatus = paymentStatus,
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned
        };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository(CreateExistingRefundOrder());
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, orders, terminal);
        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(attempt.OperationGuid, result.Order!.OrderGuid);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.Status);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
        Assert.Null(saved.RecoveryTargetStatus);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("device")]
    [InlineData("line")]
    [InlineData("amount")]
    [InlineData("card")]
    [InlineData("underpaid")]
    public async Task RecoverLatestAsync_finalize_pending_failed_refund_with_same_order_guid_mismatch_stays_locked(
        string mismatch)
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentStatus = "FAILED",
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned
        };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository(CreateMismatchedRefundOrder(mismatch));
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, orders, terminal);
        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        // 同 GUID 不是充分证据：身份、退款来源/金额或付款方法不一致都必须继续锁定。
        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, saved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_finalize_pending_failed_refund_with_matching_voucher_extra_tender_completes_without_square_lookup()
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentStatus = "FAILED",
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned
        };
        var voucherPayment = new LocalPayment(
            Guid.NewGuid(),
            PaymentMethodKind.Voucher,
            -10m,
            "VOUCHER:REFUND-001");
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository(
            CreateExistingRefundOrder(payments: [voucherPayment]));
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, orders, terminal);
        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.True(cart.IsEmpty);
        Assert.Null(cart.RecoveryOwnerAttemptGuid);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.Status);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
        Assert.Null(saved.RecoveryTargetStatus);
    }

    [Fact]
    public async Task RecoverLatestAsync_finalize_pending_failed_refund_with_pending_voucher_restores_idempotent_retry()
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentStatus = "FAILED",
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned
        };
        var pendingVoucher = new LocalPayment(
            Guid.Parse("44444444-5555-6666-7777-888888888888"),
            PaymentMethodKind.Voucher,
            -10m,
            "VOUCHER_REFUND_PENDING",
            IdempotencyKey: "refund-voucher-pending-001");
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository(
            CreateExistingRefundOrder(payments: [pendingVoucher]));
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, orders, terminal);

        var cart = new PosCartService();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.True(result.RequiresAlternativeRefundMethod);
        var restoredTender = Assert.Single(result.RestoredTenders!);
        Assert.Equal(PaymentMethodKind.Voucher, restoredTender.Method);
        Assert.Equal(-10m, restoredTender.Amount);
        Assert.Equal("VOUCHER_REFUND_PENDING", restoredTender.Reference);
        Assert.Equal("refund-voucher-pending-001", restoredTender.IdempotencyKey);
        Assert.Equal(attempt.AttemptGuid, cart.RecoveryOwnerAttemptGuid);
        Assert.Single(cart.Lines);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, saved.Status);
        Assert.Equal(CardRecoveryPhases.FinalizePending, saved.RecoveryPhase);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.RecoveryTargetStatus);
    }

    [Fact]
    public void MatchesPersistedAlternativeRefundOrder_allows_pending_to_issued_voucher_reference_update()
    {
        var paymentGuid = Guid.Parse("55555555-6666-7777-8888-999999999999");
        const string idempotencyKey = "refund-voucher-transition-001";
        var draft = CreateRefundDraft("SQ:PAYMENT-001") with
        {
            CurrentTenders =
            [
                new PaymentTender(
                    PaymentMethodKind.Voucher,
                    -6m,
                    "VOUCHER_REFUND_PENDING",
                    IdempotencyKey: idempotencyKey)
            ]
        };
        var pendingVoucher = new LocalPayment(
            paymentGuid,
            PaymentMethodKind.Voucher,
            -6m,
            "VOUCHER_REFUND_PENDING",
            IdempotencyKey: idempotencyKey);
        var issuedVoucher = pendingVoucher with { Reference = "VOUCHER_REFUND:RF-TRANSITION" };
        var order = CreateExistingRefundOrder(
            payments:
            [
                issuedVoucher,
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, -4m, "CASH:REFUND")
            ]);
        var attempt = CreateSquareRefundAttempt() with
        {
            OperationGuid = draft.OrderGuid,
            OrderDraftJson = JsonSerializer.Serialize(draft, JsonOptions)
        };

        Assert.Equal(pendingVoucher.PaymentGuid, issuedVoucher.PaymentGuid);
        Assert.Equal(pendingVoucher.Amount, issuedVoucher.Amount);
        Assert.Equal(pendingVoucher.IdempotencyKey, issuedVoucher.IdempotencyKey);
        Assert.True(SquarePaymentRecoveryService.MatchesPersistedAlternativeRefundOrder(attempt, draft, order));
    }

    [Fact]
    public async Task RecoverLatestAsync_finalize_pending_failed_refund_after_voucher_reference_update_completes()
    {
        var attempt = CreateSquareRefundAttempt() with
        {
            Status = LocalSquarePaymentAttemptStatus.Unknown,
            PaymentStatus = "FAILED",
            RecoveryPhase = CardRecoveryPhases.FinalizePending,
            RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned
        };
        var pendingVoucher = new LocalPayment(
            Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
            PaymentMethodKind.Voucher,
            -10m,
            "VOUCHER_REFUND_PENDING",
            IdempotencyKey: "refund-voucher-issued-001");
        var issuedVoucher = pendingVoucher with { Reference = "VOUCHER_REFUND:RF-ISSUED" };
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository(
            CreateExistingRefundOrder(payments: [issuedVoucher]));
        var terminal = new FakeSquareTerminalPaymentClient();
        var service = CreateService(attempts, orders, terminal);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, terminal.GetRefundCallCount);
        var saved = Assert.IsType<LocalSquarePaymentAttempt>(await attempts.GetAttemptAsync(attempt.AttemptGuid));
        Assert.Equal(LocalSquarePaymentAttemptStatus.Abandoned, saved.Status);
        Assert.Equal(CardRecoveryPhases.None, saved.RecoveryPhase);
        Assert.Null(saved.RecoveryTargetStatus);
    }

    private static SquarePaymentRecoveryService CreateService(
        FakeSquarePaymentAttemptRepository attempts,
        FakeLocalOrderRepository orders,
        FakeSquareTerminalPaymentClient terminal,
        ISharedHeldOrderRepository? sharedHeldOrderRepository = null)
    {
        return new SquarePaymentRecoveryService(
            attempts,
            new FakeSquareCardTerminalSettingsProvider(),
            terminal,
            new CashCheckoutService(),
            orders,
            sharedHeldOrderRepository: sharedHeldOrderRepository);
    }

    private static LocalSquarePaymentAttempt CreateSquareAttempt(
        LocalSquarePaymentAttemptStatus status,
        string? checkoutId)
    {
        var now = DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");
        return new LocalSquarePaymentAttempt(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            checkoutId,
            "idem-square-001",
            "DEVICE-001",
            "LOCATION-001",
            "Sandbox",
            10m,
            1000,
            "AUD",
            status,
            checkoutId is null ? null : "COMPLETED",
            null,
            JsonSerializer.Serialize(CreateDraft(), JsonOptions),
            "S001",
            "POS-01",
            "C001",
            null,
            null,
            null,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            null,
            null,
            null);
    }

    private static LocalSquarePaymentAttempt CreateSquareRefundAttempt()
    {
        var draft = CreateRefundDraft("SQ:PAYMENT-001");
        return CreateSquareAttempt(LocalSquarePaymentAttemptStatus.Recovering, checkoutId: null) with
        {
            OperationKind = "Refund",
            OperationGuid = draft.OrderGuid,
            OrderDraftJson = JsonSerializer.Serialize(draft, JsonOptions),
            PaymentId = "REFUND-001",
            PaymentStatus = "PENDING",
            SubmissionToken = "refund-submission-001"
        };
    }

    private static CardPaymentOrderDraft CreateRefundDraft(string originalReference)
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-10",
            null,
            "Returned Item",
            "930REFUND10",
            "ITEM-REFUND-10",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-REFUND-10",
            Guid.Parse("22222222-3333-4444-5555-666666666666"),
            Guid.Parse("33333333-4444-5555-6666-777777777777")));
        return new CardPaymentOrderDraft(
            CreateDraftOrderGuid(),
            Session,
            cart.CreateSnapshot(),
            [],
            cart.ActualAmount,
            10m,
            "R",
            originalReference,
            DateTimeOffset.Parse("2026-06-05T10:00:00+10:00"));
    }

    private static CardPaymentOrderDraft CreateDraft()
    {
        return new CardPaymentOrderDraft(
            CreateDraftOrderGuid(),
            Session,
            new PosCartSnapshot(
            [
                new PosCartLineSnapshot(
                    "S001",
                    "SKU-10",
                    null,
                    "Test Item",
                    "930010",
                    "ITEM-10",
                    null,
                    1m,
                    10m,
                    0m,
                    null,
                    PriceSourceKind.StoreRetailPrice,
                    "Store price")
            ]),
            [],
            10m,
            10m,
            "P",
            null,
            DateTimeOffset.Parse("2026-06-05T10:00:00+10:00"));
    }

    private static Guid CreateDraftOrderGuid()
    {
        return Guid.Parse("11111111-2222-3333-4444-555555555555");
    }

    /// <summary>订单已保存（与 claim 同事务绑定完成）、attempt 未收尾的既有订单。</summary>
    private static LocalOrder CreateExistingOrder()
    {
        return new LocalOrder(
            CreateDraftOrderGuid(),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-06-05T10:00:00+10:00"),
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(
                    Guid.NewGuid(),
                    "SKU-10",
                    null,
                    "Test Item",
                    "930010",
                    "ITEM-10",
                    1m,
                    10m,
                    0m,
                    10m,
                    PriceSourceKind.StoreRetailPrice)
            ],
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Card,
                    10m,
                    "SQ:PAYMENT-EXISTING",
                    IdempotencyKey: "SQUARE_ATTEMPT:bbbbbbbbccccddddeeeeffffffffffff")
            ]);
    }

    private static LocalOrder CreateExistingRefundOrder(
        string? storeCode = null,
        string? deviceCode = null,
        string? returnSourceKey = null,
        decimal? actualAmount = null,
        IReadOnlyList<LocalPayment>? payments = null)
    {
        var draft = CreateRefundDraft("SQ:PAYMENT-001");
        var snapshot = returnSourceKey is null
            ? draft.CartSnapshot
            : draft.CartSnapshot with
            {
                Lines = draft.CartSnapshot.Lines
                    .Select(line => line with { ReturnSourceKey = returnSourceKey })
                    .ToArray()
            };
        var recoveryCart = new PosCartService();
        recoveryCart.RestoreSnapshot(snapshot);
        var order = new CashCheckoutService()
            .CreatePaymentOrder(
                recoveryCart,
                Session,
                [new PaymentTender(PaymentMethodKind.Cash, -10m)],
                cashTenderedAmount: -10m)
            .Order;

        return order with
        {
            OrderGuid = draft.OrderGuid,
            StoreCode = storeCode ?? order.StoreCode,
            DeviceCode = deviceCode ?? order.DeviceCode,
            ActualAmount = actualAmount ?? order.ActualAmount,
            Payments = payments ?? order.Payments
        };
    }

    private static LocalSquarePaymentAttempt CreateSquareSupervisorNotPaidSaleAttempt()
    {
        var draft = CreateSaleDraftWithTenders();
        return CreateSquareAttempt(LocalSquarePaymentAttemptStatus.Pending, checkoutId: null) with
        {
            OperationKind = "Sale",
            OperationGuid = draft.OrderGuid,
            OrderDraftJson = JsonSerializer.Serialize(draft, JsonOptions),
            ResponseCode = ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid,
            ResponseText = "Supervisor confirmed that no payment was processed.",
            PaymentId = null,
            PaymentStatus = null
        };
    }

    private static LocalSquarePaymentAttempt CreateSquareCanceledSaleAttempt()
    {
        var draft = CreateSaleDraftWithTenders();
        return CreateSquareAttempt(LocalSquarePaymentAttemptStatus.Recovering, checkoutId: "CHECKOUT-001") with
        {
            OperationKind = "Sale",
            OperationGuid = draft.OrderGuid,
            OrderDraftJson = JsonSerializer.Serialize(draft, JsonOptions),
            ResponseCode = null,
            ResponseText = null,
            PaymentId = null,
            PaymentStatus = null
        };
    }

    private static CardPaymentOrderDraft CreateSaleDraftWithTenders()
    {
        return CreateDraft() with
        {
            CurrentTenders =
            [
                new PaymentTender(PaymentMethodKind.Cash, 4m, "CASH:SALE-001"),
                new PaymentTender(
                    PaymentMethodKind.Voucher,
                    6m,
                    "VOUCHER:SALE-001",
                    IdempotencyKey: "voucher-sale-001")
            ]
        };
    }

    private static LocalOrder CreateMismatchedRefundOrder(string mismatch)
    {
        var order = CreateExistingRefundOrder();
        return mismatch switch
        {
            "store" => order with { StoreCode = "S002" },
            "device" => order with { DeviceCode = "POS-02" },
            "line" => order with
            {
                Lines = [order.Lines[0] with { ReturnSourceKey = "RETURN-WRONG" }]
            },
            "amount" => order with { ActualAmount = -9m },
            "card" => order with
            {
                Payments =
                [
                    new LocalPayment(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -10m,
                        "CARD:UNEXPECTED")
                ]
            },
            "underpaid" => order with
            {
                Payments =
                [
                    new LocalPayment(
                        Guid.NewGuid(),
                        PaymentMethodKind.Cash,
                        -9m,
                        "CASH:UNDERPAID")
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, "Unknown refund mismatch test case.")
        };
    }

    /// <summary>与 CreateDraft 冻结购物车（SKU-10, $10.00）逐行一致的 claim canonical。</summary>
    private static SharedHeldOrderCanonicalPayload CanonicalForDraftCart(decimal quantity = 1m)
    {
        return new SharedHeldOrderCanonicalPayload(
            1,
            new SharedHeldOrderPricingState(
                1,
                SharedHeldOrderCanonicalConstants.SaleMode,
                "2026-07-28T00:00:00.000Z",
                [],
                [
                    new SharedHeldOrderPricingLine(
                        "line-1",
                        "SKU-10",
                        "ITEM-10",
                        "930010",
                        "Test Item",
                        quantity,
                        1000,
                        SharedHeldOrderCanonicalConstants.BasePriceSourceCatalog,
                        new SharedHeldOrderLineSyncProvenance(null, (int)PriceSourceKind.StoreRetailPrice),
                        SharedHeldOrderCanonicalConstants.LineKindSale,
                        null,
                        null,
                        null,
                        new SharedHeldOrderDiscountState(SharedHeldOrderCanonicalConstants.DiscountNone))
                ]));
    }

    private sealed class FakeSquarePaymentAttemptRepository(LocalSquarePaymentAttempt? attempt)
        : ILocalSquarePaymentAttemptRepository
    {
        private LocalSquarePaymentAttempt? _attempt = attempt;

        public LocalSquarePaymentAttemptStatus Status { get; private set; } = attempt?.Status ?? LocalSquarePaymentAttemptStatus.Failed;

        public int MarkOrderCompletedCount { get; private set; }

        public string? PaymentStatus => _attempt?.PaymentStatus;

        public int PersistRefundFailureForFinalizationCount { get; private set; }

        public bool PersistRefundFailureForFinalizationResult { get; init; } = true;

        public LocalSquarePaymentAttempt? RefundFailureCasWinner { get; init; }

        public bool FailRecoveryFinalization { get; init; }

        public Exception? GetAttemptException { get; set; }

        public bool RefundIdBoundToOtherAttempt { get; init; }

        public Task<bool> IsRefundIdBoundToOtherAttemptAsync(
            string environment,
            string refundId,
            Guid excludingAttemptGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RefundIdBoundToOtherAttempt);

        public Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> TryRecordRefundResponseAsync(
            Guid attemptGuid,
            string submissionToken,
            string refundId,
            string refundStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is not null)
            {
                _attempt = _attempt with
                {
                    Status = LocalSquarePaymentAttemptStatus.Recovering,
                    PaymentId = refundId,
                    PaymentStatus = refundStatus,
                    UpdatedAt = updatedAt
                };
                Status = _attempt.Status;
            }

            return Task.FromResult(true);
        }

        public Task<bool> TryRecordRefundResponseAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            string refundId,
            string refundStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                !string.Equals(_attempt.OperationKind, "Refund", StringComparison.Ordinal) ||
                !string.Equals(_attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                _attempt.Status is not (LocalSquarePaymentAttemptStatus.Recovering or
                    LocalSquarePaymentAttemptStatus.Unknown) ||
                string.Equals(_attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                _attempt.ResponseCode is CardRefundSupervisorResolutionCodes.ConfirmedRefunded or
                    CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded)
            {
                return Task.FromResult(false);
            }

            _attempt = _attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                PaymentId = refundId,
                PaymentStatus = refundStatus,
                UpdatedAt = updatedAt
            };
            Status = _attempt.Status;
            return Task.FromResult(true);
        }

        public Task<bool> TryPersistRefundFailureForFinalizationAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(paymentStatus, "FAILED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(paymentStatus, "REJECTED", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Square 退款失败终态必须是 FAILED 或 REJECTED。", nameof(paymentStatus));
            }

            PersistRefundFailureForFinalizationCount++;
            if (!PersistRefundFailureForFinalizationResult)
            {
                if (RefundFailureCasWinner is not null)
                {
                    _attempt = RefundFailureCasWinner;
                    Status = _attempt.Status;
                }

                return Task.FromResult(false);
            }

            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                !string.Equals(_attempt.OperationKind, "Refund", StringComparison.Ordinal) ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(_attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                _attempt.Status is LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.OrderCompleted or
                    LocalSquarePaymentAttemptStatus.Abandoned ||
                !string.Equals(
                    _attempt.RecoveryPhase ?? CardRecoveryPhases.None,
                    CardRecoveryPhases.None,
                    StringComparison.Ordinal) ||
                string.Equals(_attempt.PaymentStatus?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(_attempt.ResponseCode) &&
                    _attempt.ResponseCode is ActiveSessionSupervisorResolutionCodes.ConfirmedPaid or
                        ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid or
                        CardRefundSupervisorResolutionCodes.ConfirmedRefunded or
                        CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded)
            {
                return Task.FromResult(false);
            }

            _attempt = _attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Unknown,
                PaymentStatus = paymentStatus.ToUpperInvariant(),
                ResponseCode = string.IsNullOrWhiteSpace(_attempt.ResponseCode) ? responseCode : _attempt.ResponseCode,
                ResponseText = string.IsNullOrWhiteSpace(_attempt.ResponseText) ? responseText : _attempt.ResponseText,
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.Abandoned,
                UpdatedAt = updatedAt
            };
            Status = _attempt.Status;
            return Task.FromResult(true);
        }

        public Task MarkCheckoutCreatedAsync(
            Guid attemptGuid,
            string checkoutId,
            string? checkoutStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Status = LocalSquarePaymentAttemptStatus.CheckoutCreated;
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (_attempt is not null && _attempt.AttemptGuid == attemptGuid)
            {
                _attempt = _attempt with
                {
                    Status = LocalSquarePaymentAttemptStatus.Recovering,
                    UpdatedAt = updatedAt
                };
                Status = _attempt.Status;
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryMarkRecoveringAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt)
            {
                return Task.FromResult(false);
            }

            _attempt = _attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            };
            Status = _attempt.Status;
            return Task.FromResult(true);
        }

        public Task UpdateCheckoutStatusAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? cancelReason,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Status = status;
            return Task.CompletedTask;
        }

        public Task MarkPaymentVerifiedAsync(
            Guid attemptGuid,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is not null && _attempt.AttemptGuid == attemptGuid)
            {
                _attempt = _attempt with
                {
                    Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                    PaymentId = paymentId,
                    PaymentStatus = paymentStatus,
                    ResponseCode = responseCode,
                    ResponseText = responseText,
                    CompletedAt = completedAt,
                    UpdatedAt = completedAt
                };
                Status = _attempt.Status;
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateCheckoutStatusAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? cancelReason,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                IsTerminalSquareStatus(_attempt.Status) ||
                !string.Equals(_attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _attempt = _attempt with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? _attempt.CheckoutStatus,
                CancelReason = cancelReason ?? _attempt.CancelReason,
                UpdatedAt = updatedAt
            };
            Status = _attempt.Status;
            return Task.FromResult(true);
        }

        public Task<bool> TryPersistPaymentVerifiedRecoveryAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                _attempt.Status is LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.OrderCompleted or
                    LocalSquarePaymentAttemptStatus.Abandoned ||
                string.Equals(_attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                _attempt.ResponseCode is ActiveSessionSupervisorResolutionCodes.ConfirmedPaid or
                    ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid or
                    CardRefundSupervisorResolutionCodes.ConfirmedRefunded or
                    CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded)
            {
                return Task.FromResult(false);
            }

            PersistPaymentVerified(paymentId, paymentStatus, responseCode, responseText, completedAt);
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkRefundPaymentVerifiedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            string submissionToken,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                !string.Equals(_attempt.OperationKind, "Refund", StringComparison.Ordinal) ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(_attempt.SubmissionToken, submissionToken, StringComparison.Ordinal) ||
                _attempt.Status is LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.OrderCompleted or
                    LocalSquarePaymentAttemptStatus.Abandoned ||
                string.Equals(_attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                _attempt.ResponseCode is CardRefundSupervisorResolutionCodes.ConfirmedRefunded or
                    CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded)
            {
                return Task.FromResult(false);
            }

            PersistPaymentVerified(paymentId, paymentStatus, responseCode, responseText, completedAt);
            return Task.FromResult(true);
        }

        public Task MarkFailedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default,
            string? cancelReason = null)
        {
            Status = status;
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            MarkOrderCompletedCount++;
            Status = LocalSquarePaymentAttemptStatus.OrderCompleted;
            return Task.CompletedTask;
        }

        public Task<bool> TryBeginRecoveryFinalizationAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus targetStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (targetStatus is not (LocalSquarePaymentAttemptStatus.Canceled or
                LocalSquarePaymentAttemptStatus.TimedOut or
                LocalSquarePaymentAttemptStatus.Failed or
                LocalSquarePaymentAttemptStatus.OrderCompleted or
                LocalSquarePaymentAttemptStatus.Abandoned))
            {
                throw new ArgumentException("Square 恢复最终目标必须是终态。", nameof(targetStatus));
            }

            if (_attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                _attempt.Status is LocalSquarePaymentAttemptStatus.Canceled or
                    LocalSquarePaymentAttemptStatus.TimedOut or
                    LocalSquarePaymentAttemptStatus.Failed or
                    LocalSquarePaymentAttemptStatus.OrderCompleted or
                    LocalSquarePaymentAttemptStatus.Abandoned ||
                !string.Equals(_attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            // 与 SQLite CAS 一样先持久化 FinalizePending，再允许完成阶段提交。
            _attempt = _attempt with
            {
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = targetStatus,
                UpdatedAt = updatedAt
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryCompleteRecoveryFinalizationAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus expectedStatus,
            DateTimeOffset expectedUpdatedAt,
            LocalSquarePaymentAttemptStatus targetStatus,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            if (FailRecoveryFinalization ||
                _attempt is null ||
                _attempt.AttemptGuid != attemptGuid ||
                _attempt.Status != expectedStatus ||
                _attempt.UpdatedAt != expectedUpdatedAt ||
                !string.Equals(_attempt.RecoveryPhase, CardRecoveryPhases.FinalizePending, StringComparison.Ordinal) ||
                _attempt.RecoveryTargetStatus != targetStatus)
            {
                return Task.FromResult(false);
            }

            _attempt = _attempt with
            {
                Status = targetStatus,
                RecoveryPhase = CardRecoveryPhases.None,
                RecoveryTargetStatus = null,
                ResolvedAt = targetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted
                    ? _attempt.ResolvedAt
                    : completedAt,
                OrderCompletedAt = targetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted
                    ? completedAt
                    : _attempt.OrderCompletedAt,
                UpdatedAt = completedAt
            };
            if (targetStatus == LocalSquarePaymentAttemptStatus.OrderCompleted)
            {
                MarkOrderCompletedCount++;
            }

            Status = _attempt.Status;
            return Task.FromResult(true);
        }

        private void PersistPaymentVerified(
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt)
        {
            _attempt = _attempt! with
            {
                Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                PaymentId = paymentId,
                PaymentStatus = paymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                CompletedAt = completedAt,
                UpdatedAt = completedAt,
                RecoveryPhase = CardRecoveryPhases.FinalizePending,
                RecoveryTargetStatus = LocalSquarePaymentAttemptStatus.OrderCompleted
            };
            Status = _attempt.Status;
        }

        public Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_attempt);
        }

        public Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default)
        {
            var attempt = _attempt;
            return Task.FromResult<IReadOnlyList<LocalSquarePaymentAttempt>>(
                attempt?.OperationKind == "Refund" &&
                (!IsTerminalSquareStatus(attempt.Status) || IsRepairableFailedRefund(attempt))
                    ? [attempt]
                    : []);
        }

        public Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            if (GetAttemptException is not null)
            {
                throw GetAttemptException;
            }

            return Task.FromResult(_attempt);
        }

        private static bool IsTerminalSquareStatus(LocalSquarePaymentAttemptStatus status) =>
            status is LocalSquarePaymentAttemptStatus.Canceled or
                LocalSquarePaymentAttemptStatus.TimedOut or
                LocalSquarePaymentAttemptStatus.Failed or
                LocalSquarePaymentAttemptStatus.OrderCompleted or
                LocalSquarePaymentAttemptStatus.Abandoned;

        private static bool IsRepairableFailedRefund(LocalSquarePaymentAttempt attempt) =>
            attempt.Status == LocalSquarePaymentAttemptStatus.Failed &&
            (string.Equals(attempt.PaymentStatus?.Trim(), "FAILED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(attempt.PaymentStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(attempt.SubmissionToken) &&
            string.Equals(attempt.RecoveryPhase, CardRecoveryPhases.None, StringComparison.Ordinal) &&
            attempt.RecoveryTargetStatus is null &&
            attempt.ResponseCode is not (
                ActiveSessionSupervisorResolutionCodes.ConfirmedPaid or
                ActiveSessionSupervisorResolutionCodes.ConfirmedNotPaid or
                ActiveSessionSupervisorResolutionCodes.ContinueWaiting or
                CardRefundSupervisorResolutionCodes.ConfirmedRefunded or
                CardRefundSupervisorResolutionCodes.ConfirmedNotRefunded or
                CardRefundSupervisorResolutionCodes.ContinueWaiting);
    }

    private static string CreateTempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hbpos-square-recovery-service-{Guid.NewGuid():N}.db");

    private static void DeleteTempDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class FakeSquareTerminalPaymentClient : ISquareTerminalPaymentClient
    {
        public SquareCheckoutStatusResult Checkout { get; set; } =
            new("CHECKOUT-001", "COMPLETED", 1000, "AUD", ["PAYMENT-001"], null);

        public SquarePaymentStatusResult Payment { get; set; } =
            new("PAYMENT-001", "COMPLETED", 1000, "AUD");

        public SquareRefundStatusResult Refund { get; set; } =
            new("REFUND-001", "COMPLETED", "PAYMENT-001", 1000, "AUD");

        public Dictionary<string, SquareRefundStatusResult> RefundsById { get; } = new(StringComparer.Ordinal);

        public int GetRefundCallCount { get; private set; }

        public int GetCheckoutCallCount { get; private set; }

        public int GetPaymentCallCount { get; private set; }

        public Task<SquareCheckoutStatusResult> GetCheckoutAsync(
            CardTerminalSettings settings,
            string checkoutId,
            CancellationToken cancellationToken = default)
        {
            GetCheckoutCallCount++;
            return Task.FromResult(Checkout);
        }

        public Task<SquarePaymentStatusResult> GetPaymentAsync(
            CardTerminalSettings settings,
            string paymentId,
            CancellationToken cancellationToken = default)
        {
            GetPaymentCallCount++;
            return Task.FromResult(Payment);
        }

        public Task<SquareRefundStatusResult> GetRefundAsync(
            CardTerminalSettings settings,
            string refundId,
            CancellationToken cancellationToken = default)
        {
            GetRefundCallCount++;
            return Task.FromResult(RefundsById.TryGetValue(refundId, out var byId) ? byId : Refund);
        }
    }

    private sealed class FakeSquareCardTerminalSettingsProvider : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CardTerminalSettings(
                CardProcessorKind.Square,
                CardTerminalEnvironment.Sandbox,
                "127.0.0.1",
                2011,
                "DEVICE-001",
                "LOCATION-001",
                "token",
                "https://connect.squareupsandbox.com",
                TimeSpan.FromSeconds(30),
                LinklyConnectionMode.LocalIp));
        }
    }

    private sealed class FakeLocalOrderRepository : ILocalOrderRepository
    {
        private LocalOrder? _saved;
        private readonly LocalOrder? _existingOrder;

        public FakeLocalOrderRepository(LocalOrder? existingOrder = null)
        {
            _existingOrder = existingOrder;
        }

        public int SaveCount { get; private set; }

        public List<(LocalOrder Order, LocalHeldOrderCompletionContext Context)> HeldSources { get; } = [];

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _saved = order;
            return Task.CompletedTask;
        }

        public async Task SavePendingOrderWithHeldSourceAsync(
            LocalOrder order,
            LocalHeldOrderCompletionContext heldOrder,
            CancellationToken cancellationToken = default)
        {
            await SavePendingOrderAsync(order, cancellationToken);
            HeldSources.Add((order, heldOrder));
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

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

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            var existing = _existingOrder is not null && _existingOrder.OrderGuid == orderGuid
                ? _existingOrder
                : null;
            return Task.FromResult(existing ?? (_saved is not null && _saved.OrderGuid == orderGuid ? _saved : null));
        }
    }
}
