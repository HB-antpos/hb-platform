using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 永久跨层回归：从真实付款页走到真实工作流、终端路由、Cloud 客户端和本地仓储。
/// </summary>
public sealed class PaymentFlowIntegrationTests
{
    [Fact]
    public async Task Late_approval_after_manual_cancel_keeps_card_tender_and_saves_card_order()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: true);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var cart = fixture.CreateSaleCart();
        using var viewModel = fixture.CreatePaymentViewModel(cart);

        var cardTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        try
        {
            await cloudApi.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            viewModel.CancelCommand.Execute(null);
            Assert.False(viewModel.CancelCommand.CanExecute(null));
            Assert.False(viewModel.SelectCashCommand.CanExecute(null));

            // 第二次取消必须没有效果；晚到批准仍由原付款调用完成并进入 Card tender。
            viewModel.CancelCommand.Execute(null);
            cloudApi.ReleaseApprovedResult();
            await cardTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, cloudApi.SendCount);
            Assert.Equal(0, cloudApi.GetTransactionCount);

            var cardTender = Assert.Single(viewModel.PaymentTenders);
            Assert.Equal(PaymentMethodKind.Card, cardTender.Method);
            Assert.Equal(10m, cardTender.Amount);
            Assert.False(viewModel.SelectCashCommand.CanExecute(null));
            Assert.False(viewModel.SelectCardCommand.CanExecute(null));

            var attemptGuid = ReadAttemptGuid(cardTender.IdempotencyKey);
            var approvedAttempt = await fixture.AttemptRepository.GetAttemptAsync(attemptGuid);
            Assert.NotNull(approvedAttempt);
            Assert.Equal(LocalCardPaymentAttemptStatus.Approved, approvedAttempt!.Status);
            Assert.Equal(cloudApi.LastSubmittedSessionId, approvedAttempt.SessionId);
            Assert.Equal(cloudApi.LastSubmittedTxnRef, approvedAttempt.TxnRef);
            var frozenDraft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
                approvedAttempt.OrderDraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(frozenDraft);

            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

            var savedSummary = Assert.Single(await fixture.OrderRepository.GetRecentOrdersAsync());
            var savedOrder = await fixture.OrderRepository.GetOrderAsync(savedSummary.OrderGuid);
            Assert.NotNull(savedOrder);
            Assert.Equal(frozenDraft!.OrderGuid, savedOrder!.OrderGuid);
            var savedPayment = Assert.Single(savedOrder!.Payments);
            Assert.Equal(PaymentMethodKind.Card, savedPayment.Method);
            Assert.Equal(10m, savedPayment.Amount);
            Assert.Equal(cardTender.IdempotencyKey, savedPayment.IdempotencyKey);

            var completedAttempt = await fixture.AttemptRepository.GetAttemptAsync(attemptGuid);
            Assert.NotNull(completedAttempt);
            Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, completedAttempt!.Status);
        }
        finally
        {
            if (viewModel.IsCardPaymentInProgress)
            {
                viewModel.CancelCommand.Execute(null);
            }
            cloudApi.ReleaseApprovedResult();
            try
            {
                await cardTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // 断言失败时只确保后台付款调用已观察并退出，原断言仍由测试框架报告。
            }
        }
    }

    [Fact]
    public async Task Direct_post_cancel_persists_attempt_and_recovery_queries_original_session_without_second_post()
    {
        var cloudApi = new ScriptedLinklyCloudApi(returnApprovalAfterRelease: false);
        await using var fixture = await PaymentFlowTestFixture.CreateAsync(cloudApi);
        var cart = fixture.CreateSaleCart();
        var recoveryCenterOpened = 0;
        using var viewModel = fixture.CreatePaymentViewModel(
            cart,
            () => Interlocked.Increment(ref recoveryCenterOpened));

        var cardTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        try
        {
            await cloudApi.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // POST 已经发出时，session/TxnRef 和冻结草稿必须先存在于持久化 attempt。
            var submittedAttempt = await fixture.AttemptRepository.GetLatestOpenAttemptAsync(
                storeCode: fixture.Session.StoreCode,
                deviceCode: fixture.Session.DeviceCode,
                cashierId: null,
                environment: fixture.Settings.Environment.ToString());
            Assert.NotNull(submittedAttempt);
            Assert.Equal(LocalCardPaymentAttemptStatus.SessionStarted, submittedAttempt!.Status);
            Assert.Equal(cloudApi.LastSubmittedSessionId, submittedAttempt.SessionId);
            Assert.Equal(cloudApi.LastSubmittedTxnRef, submittedAttempt.TxnRef);
            Assert.False(string.IsNullOrWhiteSpace(submittedAttempt.OrderDraftJson));

            // 手动取消时终端结果确认不了（取消键后查询不可用）：保守按结果未知进异常中心。
            cloudApi.StatusQueriesUnavailable = true;
            viewModel.CancelCommand.Execute(null);
            await cardTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(viewModel.IsCardPaymentRecoveryRequired);
            Assert.True(viewModel.OpenCardRecoveryCenterCommand.CanExecute(null));
            viewModel.OpenCardRecoveryCenterCommand.Execute(null);
            Assert.Equal(1, recoveryCenterOpened);

            var attempt = await fixture.AttemptRepository.GetLatestOpenAttemptAsync(
                storeCode: fixture.Session.StoreCode,
                deviceCode: fixture.Session.DeviceCode,
                cashierId: null,
                environment: fixture.Settings.Environment.ToString());
            Assert.NotNull(attempt);
            Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempt!.Status);
            Assert.Equal(nameof(LinklyConnectionMode.CloudDirectSync), attempt.ConnectionMode);
            Assert.False(string.IsNullOrWhiteSpace(attempt.SessionId));
            Assert.False(string.IsNullOrWhiteSpace(attempt.TxnRef));
            Assert.Equal(cloudApi.LastSubmittedSessionId, attempt.SessionId);
            Assert.Equal(cloudApi.LastSubmittedTxnRef, attempt.TxnRef);
            Assert.Equal(1, cloudApi.SendCount);

            // 冻结草稿包含恢复所需的原订单身份；恢复服务随后只能查询该身份。
            var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
                attempt.OrderDraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(draft);
            Assert.NotEqual(Guid.Empty, draft!.OrderGuid);
            Assert.Equal(fixture.Session.StoreCode, draft.Session.StoreCode);
            Assert.Equal(10m, draft.CardAmount);
            Assert.Empty(await fixture.OrderRepository.GetRecentOrdersAsync());

            cloudApi.StatusQueriesUnavailable = false;
            await using var restarted = await fixture.CreateRestartedRecoveryAsync();
            var persistedAfterRestart = await restarted.AttemptRepository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.NotNull(persistedAfterRestart);
            Assert.Equal(attempt.SessionId, persistedAfterRestart!.SessionId);
            Assert.Equal(attempt.TxnRef, persistedAfterRestart.TxnRef);

            var recovery = await restarted.Service.RecoverAttemptAsync(
                attempt.AttemptGuid,
                new PosCartService(),
                fixture.Session);

            Assert.True(recovery.Outcome == CardPaymentRecoveryOutcome.OrderCompleted,
                $"Recovery outcome={recovery.Outcome}, POST={cloudApi.SendCount}, GET={cloudApi.GetTransactionCount}: {recovery.Message}");
            Assert.Equal(1, cloudApi.SendCount);
            Assert.Equal(1, cloudApi.GetTransactionCount);
            Assert.Equal(attempt.SessionId, cloudApi.LastQueriedSessionId);

            var recoveredSummary = Assert.Single(await restarted.OrderRepository.GetRecentOrdersAsync());
            Assert.Equal(draft.OrderGuid, recoveredSummary.OrderGuid);
            var recoveredOrder = await restarted.OrderRepository.GetOrderAsync(draft.OrderGuid);
            Assert.NotNull(recoveredOrder);
            var recoveredPayment = Assert.Single(recoveredOrder!.Payments);
            Assert.Equal(PaymentMethodKind.Card, recoveredPayment.Method);
            Assert.Equal(10m, recoveredPayment.Amount);
            Assert.Equal($"CARD_ATTEMPT:{attempt.AttemptGuid:N}", recoveredPayment.IdempotencyKey);

            var completedAttempt = await restarted.AttemptRepository.GetAttemptAsync(attempt.AttemptGuid);
            Assert.NotNull(completedAttempt);
            Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, completedAttempt!.Status);
        }
        finally
        {
            if (viewModel.IsCardPaymentInProgress)
            {
                viewModel.CancelCommand.Execute(null);
            }
            try
            {
                await cardTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // 断言失败时只确保后台付款调用已观察并退出，原断言仍由测试框架报告。
            }
        }
    }

    private static Guid ReadAttemptGuid(string? idempotencyKey)
    {
        const string prefix = "CARD_ATTEMPT:";
        Assert.StartsWith(prefix, idempotencyKey);
        Assert.True(Guid.TryParse(idempotencyKey![prefix.Length..], out var attemptGuid));
        return attemptGuid;
    }
}
