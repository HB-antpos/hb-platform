using System.Net;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class InstallmentRepaymentClaimOperationServiceTests
{
    [Fact]
    public async Task Card_repayment_claims_before_terminal_and_commits_without_legacy_append()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRequest(PaymentMethodKind.Card);
            var context = new LinklyPaymentAttemptContextAccessor();
            var api = new ClaimApi(request, events);
            var terminal = new RecordingTerminal(
                events,
                new PaymentAuthorizationResult(false),
                amount =>
                {
                    // 终端回执与卡交易必须引用同一次 Linkly 尝试生成的交易号。
                    var txnRef = context.Current?.TxnRef ?? throw new InvalidOperationException("Linkly 尝试上下文未提供交易号。");
                    return new PaymentAuthorizationResult(
                        true,
                        $"ANZ:{txnRef}",
                        AuthorizedAmount: amount,
                        CardTransactions: [CreateCardTransaction(amount, txnRef)],
                        Processor: "Linkly");
                });
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(["claim:create", "claim:begin:Linkly", "provider:card", "claim:commit"], events);
            Assert.Equal(0, api.LegacyAppendCalls);
            Assert.Equal(request.PaymentGuid, api.LastCreate!.OperationGuid);
            Assert.Equal(request.PaymentGuid, api.LastCreate.PaymentGuid);
            Assert.Equal(request.IdempotencyKey, api.LastCreate.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash, "Cash")]
    [InlineData(PaymentMethodKind.Voucher, "Voucher")]
    public async Task Non_card_repayment_still_begins_claim_provider_before_commit(PaymentMethodKind method, string provider)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(method) with
            {
                Reference = method == PaymentMethodKind.Voucher ? "VOUCHER-1" : null,
                ReservationToken = method == PaymentMethodKind.Voucher ? "reservation-1" : null
            };
            var api = new ClaimApi(request, events);
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(false));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(["claim:create", $"claim:begin:{provider}", "claim:commit"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(0, api.LegacyAppendCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Busy_claim_stops_before_card_provider()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events)
            {
                CreateException = new CatalogApiException("busy", HttpStatusCode.Conflict, "INSTALLMENT_REPAYMENT_BUSY")
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(true, AuthorizedAmount: request.Amount));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.Equal(["claim:create"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(0, api.LegacyAppendCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unsupported_card_method_fails_cleanly_before_provider_without_review_lock()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events)
            {
                CreateException = new CatalogApiException(
                    "Card installment repayment is unavailable until provider evidence can be verified by the server.",
                    HttpStatusCode.BadRequest,
                    "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED")
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(true, AuthorizedAmount: request.Amount));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            // 服务端在写 claim 前就拒绝：本地操作必须结案为 Failed 且不要求人工核对，付款页才能清掉草稿 tender。
            Assert.False(result.Succeeded);
            Assert.False(result.RequiresReview);
            Assert.Contains("暂不支持刷卡", result.Message);
            Assert.Equal(["claim:create"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(0, api.LegacyAppendCalls);
            var operation = await repository.GetAsync(request.PaymentGuid);
            Assert.Equal(LocalInstallmentOperationState.Failed, operation?.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Card_repayment_on_a_processor_the_server_has_not_enabled_releases_the_claim_before_the_terminal()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events)
            {
                Capabilities = new InstallmentRepaymentCapabilitiesResponse(
                    true,
                    false,
                    true,
                    120,
                    CardRepaymentSupported: true,
                    CardRepaymentProviders: ["square"])
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(true, AuthorizedAmount: request.Amount));
            var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings));

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            // 中央 claim 还没登记通道：当场释放并结案，付款页可以清掉草稿，不必等 120 秒过期。
            Assert.False(result.Succeeded);
            Assert.False(result.RequiresReview);
            Assert.Contains("暂不支持分期续付", result.Message);
            Assert.Equal(["claim:create", "claim:resolve:Released"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(InstallmentRepaymentClaimStatus.Released, api.Status);
            var operation = await repository.GetAsync(request.PaymentGuid);
            Assert.Equal(LocalInstallmentOperationState.Failed, operation?.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Begin_provider_rejected_as_not_enabled_fails_cleanly_and_releases_the_claim()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events)
            {
                BeginException = new CatalogApiException(
                    "Card installment repayment is not enabled for provider 'Linkly'.",
                    HttpStatusCode.BadRequest,
                    "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED")
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(true, AuthorizedAmount: request.Amount));
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.False(result.RequiresReview);
            Assert.Contains("暂不支持分期续付", result.Message);
            Assert.Equal(["claim:create", "claim:begin:Linkly", "claim:resolve:Released"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            var operation = await repository.GetAsync(request.PaymentGuid);
            Assert.Equal(LocalInstallmentOperationState.Failed, operation?.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Zeller_repayment_binds_the_claim_before_the_sdk_and_commits_the_approved_transaction()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events);
            var zeller = new ZellerRepaymentRig(path);
            string? sentReference = null;
            zeller.Gateway.OnPurchase = (purchase, _, _) =>
            {
                // 中文注释：调用 SDK 时中央 claim 必须已经登记了 Zeller 通道和这一条尝试。
                events.Add($"zeller:purchase:{purchase.AmountCents}");
                sentReference = purchase.Reference;
                return Task.FromResult(ZellerCallResult.FromTransaction(ZellerApproved(purchase.Reference, purchase.AmountCents)));
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(false));
            var service = zeller.CreateService(repository, api, terminal);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(["claim:create", "claim:begin:Zeller", "zeller:purchase:4000", "claim:commit"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            var attempt = Assert.Single(await zeller.Attempts.ListRecentAsync(Session.StoreCode, Session.DeviceCode, 10));
            Assert.Equal(api.ProviderAttemptId, attempt.AttemptGuid.ToString("D"));
            Assert.Equal(ZellerAttemptOperation.InstallmentRepayment, attempt.OperationKind);
            Assert.Equal(ZellerAttemptStatus.OrderCompleted, attempt.Status);
            Assert.Equal(request.PaymentGuid, attempt.OperationGuid);
            var commit = api.LastCommittedRequest!;
            Assert.Equal($"ZELLER:{sentReference}:{ZellerTransactionUuid}", commit.Reference);
            var transaction = Assert.Single(commit.CardTransactions!);
            Assert.Equal("Zeller", transaction.Processor);
            Assert.Equal(ZellerTransactionUuid, transaction.TxnRef);
            Assert.Equal(40m, transaction.Amount);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Zeller_not_paid_awaiting_late_recheck_keeps_the_claim_open_until_recovery_confirms_it()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events);
            var zeller = new ZellerRepaymentRig(path);
            zeller.Gateway.OnPurchase = (_, _, _) =>
                Task.FromResult(ZellerCallResult.FromError(ZellerErrorType.Cancelled, reachedTerminal: true));
            zeller.Gateway.OnQuery = (_, _) => ZellerQueryResult.Found();
            var service = zeller.CreateService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)));

            var first = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            // 中文注释：查到 0 笔但 60 秒后还要复核：分期 claim 不能按拒绝结案（结案后迟到的批准就无法入账）。
            Assert.False(first.Succeeded);
            Assert.True(first.RequiresReview);
            Assert.Contains("复核", first.Message);
            Assert.Equal(InstallmentRepaymentClaimStatus.Unknown, api.Status);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(request.PaymentGuid))?.State);
            Assert.Equal(0, api.CommitCalls);

            // 复核时间已到（尝试表用的假时钟早于真实时钟）：恢复时补做复核，仍是 0 笔才按拒绝结案。
            var recovered = Assert.Single(await service.RecoverAsync(Session));

            Assert.Equal(LocalInstallmentOperationState.Failed, recovered.State);
            Assert.Equal(InstallmentRepaymentClaimStatus.Declined, api.Status);
            Assert.Equal(1, zeller.Gateway.Calls.ToArray().Count(call => call.StartsWith("purchase:", StringComparison.Ordinal)));
            var attempt = Assert.Single(await zeller.Attempts.ListRecentAsync(Session.StoreCode, Session.DeviceCode, 10));
            Assert.Equal(ZellerAttemptStatus.Canceled, attempt.Status);
            Assert.NotNull(attempt.LateRecheckedAt);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Zeller_unknown_repayment_recovers_by_query_only_and_commits_without_a_second_purchase()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events);
            // 中文注释：结果未知时客户端按真实时钟重查；窗口压到几毫秒，让这一笔很快交给恢复。
            var zeller = new ZellerRepaymentRig(
                path,
                new ZellerClientOptions
                {
                    VerificationInterval = TimeSpan.FromMilliseconds(1),
                    VerificationWindow = TimeSpan.FromMilliseconds(5)
                },
                TimeProvider.System);
            var queryFindsApproval = false;
            zeller.Gateway.OnPurchase = (_, _, _) => Task.FromResult(ZellerCallResult.Faulted("network dropped"));
            zeller.Gateway.OnQuery = (reference, _) => queryFindsApproval
                ? ZellerQueryResult.Found(ZellerApproved(reference, 4000))
                : ZellerQueryResult.Failed(ZellerErrorType.NetworkFailure);
            var service = zeller.CreateService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)));

            var first = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);
            Assert.False(first.Succeeded);
            Assert.True(first.RequiresReview);

            queryFindsApproval = true;
            var recovered = Assert.Single(await service.RecoverAsync(Session));

            Assert.Equal(LocalInstallmentOperationState.Completed, recovered.State);
            Assert.Equal(1, api.CommitCalls);
            Assert.Equal(1, zeller.Gateway.Calls.ToArray().Count(call => call.StartsWith("purchase:", StringComparison.Ordinal)));
            Assert.Equal(ZellerTransactionUuid, Assert.Single(api.LastCommittedRequest!.CardTransactions!).TxnRef);
            var attempt = Assert.Single(await zeller.Attempts.ListRecentAsync(Session.StoreCode, Session.DeviceCode, 10));
            Assert.Equal(ZellerAttemptStatus.OrderCompleted, attempt.Status);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Zeller_repayment_below_one_dollar_releases_the_claim_before_the_sdk()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card) with { Amount = 0.99m };
            var api = new ClaimApi(request, events);
            var zeller = new ZellerRepaymentRig(path);
            var service = zeller.CreateService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)));

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.False(result.RequiresReview);
            Assert.Contains("$1.00", result.Message);
            Assert.Equal(["claim:create", "claim:resolve:Released"], events);
            Assert.Empty(zeller.Gateway.Calls.ToArray());
            Assert.Empty(await zeller.Attempts.ListRecentAsync(Session.StoreCode, Session.DeviceCode, 10));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Zeller_purchase_with_an_existing_fixed_attempt_is_never_sent_again()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var zeller = new ZellerRepaymentRig(path);
            var attemptGuid = Guid.NewGuid();
            var context = new ZellerPurchaseContext(4000, "{}", "S001", "POS-01", "C001")
            {
                AttemptGuid = attemptGuid,
                OperationKind = ZellerAttemptOperation.InstallmentRepayment
            };
            zeller.Gateway.OnPurchase = (purchase, _, _) =>
                Task.FromResult(ZellerCallResult.FromTransaction(ZellerApproved(purchase.Reference, purchase.AmountCents)));
            await zeller.Client.PurchaseAsync(context);

            await Assert.ThrowsAsync<InvalidOperationException>(() => zeller.Client.PurchaseAsync(context));

            Assert.Single(zeller.Gateway.Calls.ToArray(), call => call.StartsWith("purchase:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    private static InstallmentAppendPaymentRequest CreateManualRequest()
    {
        var request = CreateRequest(PaymentMethodKind.Card);
        return request with { Reference = ManualCardPaymentReference.Format(request.PaymentGuid) };
    }

    [Fact]
    public async Task Manual_card_repayment_holds_the_claim_first_and_commits_cashier_confirmed_evidence()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateManualRequest();
            var api = new ClaimApi(request, events);
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(false));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var begin = await service.BeginManualCardRepaymentAsync(Session, request);

            Assert.True(begin.Succeeded, begin.Message);
            Assert.Equal(["claim:create", "claim:begin:Manual"], events);
            Assert.Equal(request.PaymentGuid.ToString("D"), api.ProviderAttemptId);
            var held = await repository.GetAsync(request.PaymentGuid);
            Assert.Equal(LocalInstallmentOperationState.TerminalSubmitting, held?.State);
            Assert.Equal("Manual", held?.TerminalProcessor);

            var result = await service.CompleteManualCardRepaymentAsync(request.PaymentGuid);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(["claim:create", "claim:begin:Manual", "claim:commit"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(request.Reference, api.LastCommittedRequest!.Reference);
            var transaction = Assert.Single(api.LastCommittedRequest.CardTransactions!);
            Assert.Equal("Manual", transaction.Processor);
            Assert.Equal(request.Reference, transaction.TxnRef);
            Assert.Equal(request.Amount, transaction.Amount);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Manual_card_repayment_cancel_declines_the_claim_and_a_failed_release_keeps_it_held()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateManualRequest();
            var api = new ClaimApi(request, events) { ResolveException = new HttpRequestException("offline") };
            var service = new InstallmentOperationService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)), new NoopVoucherTenderClient());
            await service.BeginManualCardRepaymentAsync(Session, request);

            Assert.False(await service.CancelManualCardRepaymentAsync(request.PaymentGuid));
            Assert.Equal(LocalInstallmentOperationState.TerminalSubmitting, (await repository.GetAsync(request.PaymentGuid))?.State);

            api.ResolveException = null;
            Assert.True(await service.CancelManualCardRepaymentAsync(request.PaymentGuid));

            Assert.Equal(InstallmentRepaymentClaimStatus.Declined, api.Status);
            Assert.Equal(LocalInstallmentOperationState.Failed, (await repository.GetAsync(request.PaymentGuid))?.State);
            Assert.Equal(0, api.CommitCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Manual_card_repayment_stops_before_any_claim_when_the_server_has_not_enabled_it()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateManualRequest();
            var api = new ClaimApi(request, events)
            {
                Capabilities = new InstallmentRepaymentCapabilitiesResponse(
                    true,
                    false,
                    true,
                    120,
                    CardRepaymentSupported: true,
                    CardRepaymentProviders: ["square", "linkly"])
            };
            var service = new InstallmentOperationService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)), new NoopVoucherTenderClient());

            var begin = await service.BeginManualCardRepaymentAsync(Session, request);

            Assert.False(begin.Succeeded);
            Assert.False(begin.RequiresReview);
            Assert.Contains("手动刷卡", begin.Message);
            Assert.Empty(events);
            Assert.Null(await repository.GetAsync(request.PaymentGuid));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Manual_card_repayment_interrupted_while_collecting_stays_locked_for_the_supervisor()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateManualRequest();
            var api = new ClaimApi(request, events);
            var service = new InstallmentOperationService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)), new NoopVoucherTenderClient());
            await service.BeginManualCardRepaymentAsync(Session, request);

            // 程序在收款窗口打开时退出：POS 无法查询独立刷卡机，既不能自动入账，也不能自动放开。
            var restarted = new InstallmentOperationService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)), new NoopVoucherTenderClient());
            var recovered = Assert.Single(await restarted.RecoverAsync(Session));

            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, recovered.State);
            Assert.Equal(InstallmentRepaymentClaimStatus.Unknown, api.Status);
            Assert.Equal(0, api.CommitCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Manual_card_repayment_whose_claim_never_reached_the_server_can_be_recreated_and_released()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateManualRequest();
            await repository.CreateOrGetAsync(CreateOperation(request, LocalInstallmentOperationState.Prepared));
            var api = new ClaimApi(request, events) { GetException = new CatalogApiException("missing", HttpStatusCode.NotFound) };
            var service = new InstallmentOperationService(repository, api, new RecordingTerminal(events, new PaymentAuthorizationResult(false)), new NoopVoucherTenderClient());

            var recovered = Assert.Single(await service.RecoverAsync(Session));

            Assert.Equal(LocalInstallmentOperationState.Failed, recovered.State);
            Assert.Equal(InstallmentRepaymentClaimStatus.Released, api.Status);
            Assert.Equal(0, api.CommitCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    private const string ZellerTransactionUuid = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

    private static ZellerTransaction ZellerApproved(string reference, int amountCents) => new(
        ZellerTransactionStatus.Approved,
        ZellerTransactionType.Purchase,
        amountCents,
        ZellerTransactionUuid,
        reference,
        ResponseCode: "00",
        ResponseText: "APPROVED",
        ApprovalCode: "456321",
        Rrn: "264753970917",
        PanMasked: "…2902",
        Scheme: "VISA",
        Bin: "483028",
        MerchantId: "000000000143281",
        TerminalId: "00134553",
        ReceiptText: "RECEIPT",
        ReceiptLink: null);

    /// <summary>分期续付用的 Zeller 组件：脚本网关、真实 SQLite 尝试表、和收款流程共用同一份「进行中」登记。</summary>
    private sealed class ZellerRepaymentRig
    {
        public ZellerRepaymentRig(string databasePath, ZellerClientOptions? options = null, TimeProvider? timeProvider = null)
        {
            var time = timeProvider ?? Time;
            Attempts = new LocalZellerPaymentAttemptRepository(new LocalSqliteStore(databasePath));
            Client = new ZellerCardTerminalClient(Gateway, Attempts, options, time);
            Recovery = new ZellerPaymentRecoveryService(Gateway, Attempts, Client, timeProvider: time);
        }

        public ScriptedZellerGateway Gateway { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero));

        public LocalZellerPaymentAttemptRepository Attempts { get; }

        public ZellerCardTerminalClient Client { get; }

        public ZellerPaymentRecoveryService Recovery { get; }

        public InstallmentOperationService CreateService(
            ILocalInstallmentOperationRepository repository,
            IInstallmentApiClient api,
            ICardTerminalClient terminal) =>
            new(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(
                    CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Zeller }),
                zellerCardTerminalClient: Client,
                zellerPaymentRecoveryService: Recovery,
                zellerPaymentAttemptRepository: Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Card_repayment_without_specific_processor_stops_before_claim_begin_and_provider(bool authorizeCard)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events);
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(
                true,
                "CARD-UNEXPECTED",
                AuthorizedAmount: request.Amount,
                CardTransactions: [CreateCardTransaction(request.Amount)]));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard);

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Contains("具体银行卡处理器", result.Message);
            Assert.Equal(["claim:create"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(0, api.CommitCalls);
            Assert.Equal(0, api.LegacyAppendCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Voucher_repayment_claims_before_redeem_and_commits_authorized_reservation()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Voucher) with { Reference = "VIP002" };
            var api = new ClaimApi(request, events);
            var voucher = new RecordingVoucherTenderClient(events, new PaymentAuthorizationResult(
                true,
                "VOUCHER:VIP002:LOCK-002",
                AuthorizedAmount: request.Amount));
            var service = new InstallmentOperationService(
                repository,
                api,
                new RecordingTerminal(events, new PaymentAuthorizationResult(false)),
                voucher);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(["claim:create", "claim:begin:Voucher", "provider:voucher", "claim:commit"], events);
            Assert.Equal("VIP002", api.LastCommittedRequest!.Reference);
            Assert.Equal("LOCK-002", api.LastCommittedRequest.ReservationToken);
            Assert.Equal(0, api.LegacyAppendCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Prepared_local_action_replays_missing_claim_create_without_new_provider_authorization()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Cash);
            await repository.CreateOrGetAsync(CreateOperation(request, LocalInstallmentOperationState.Prepared));
            var api = new ClaimApi(request, events)
            {
                GetException = new CatalogApiException("not found", HttpStatusCode.NotFound, "INSTALLMENT_REPAYMENT_CLAIM_NOT_FOUND")
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(false));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var recovered = await service.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(["claim:get", "claim:create", "claim:begin:Cash", "claim:commit"], events);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(LocalInstallmentOperationState.TerminalApproved)]
    [InlineData(LocalInstallmentOperationState.ApiSubmitting)]
    [InlineData(LocalInstallmentOperationState.ResultUnknown)]
    public async Task Missing_claim_after_provider_boundary_requires_manual_reconciliation_without_recreating(
        LocalInstallmentOperationState state)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Card);
            await repository.CreateOrGetAsync(CreateOperation(request, state));
            var api = new ClaimApi(request, events)
            {
                GetException = new CatalogApiException("not found", HttpStatusCode.NotFound, "INSTALLMENT_REPAYMENT_CLAIM_NOT_FOUND")
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(
                true,
                "ANZ:CARD-UNEXPECTED",
                AuthorizedAmount: request.Amount,
                CardTransactions: [CreateCardTransaction(request.Amount)],
                Processor: "Linkly"));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var recovered = Assert.Single(await service.RecoverAsync(Session));

            Assert.False(recovered.ReplayedApi);
            Assert.Contains("人工对账", recovered.Message);
            Assert.Equal(["claim:get"], events);
            Assert.Null(api.LastCreate);
            Assert.Equal(0, api.CommitCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.NotEqual(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("card-approval")]
    [InlineData("voucher-reservation")]
    public async Task Prepared_local_action_with_provider_evidence_does_not_recreate_missing_claim(string evidence)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = evidence switch
            {
                "card-approval" => CreateRequest(PaymentMethodKind.Card) with
                {
                    Reference = "ANZ:CARD-APPROVED",
                    CardTransactions = [CreateCardTransaction(40m)]
                },
                "voucher-reservation" => CreateRequest(PaymentMethodKind.Voucher) with
                {
                    Reference = "VIP004",
                    ReservationToken = "reservation-existing"
                },
                _ => CreateRequest(PaymentMethodKind.Card)
            };
            var operation = CreateOperation(request, LocalInstallmentOperationState.Prepared) with
            {
                TerminalAttemptGuid = evidence == "attempt" ? Guid.NewGuid().ToString("D") : null,
                TerminalProcessor = evidence == "attempt" ? "Linkly" : null
            };
            await repository.CreateOrGetAsync(operation);
            var api = new ClaimApi(request, events)
            {
                GetException = new CatalogApiException("not found", HttpStatusCode.NotFound, "INSTALLMENT_REPAYMENT_CLAIM_NOT_FOUND")
            };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(true, AuthorizedAmount: request.Amount));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var recovered = Assert.Single(await service.RecoverAsync(Session));

            Assert.False(recovered.ReplayedApi);
            Assert.Contains("人工对账", recovered.Message);
            Assert.Equal(["claim:get"], events);
            Assert.Null(api.LastCreate);
            Assert.Equal(0, api.CommitCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unknown_claim_resumes_same_binding_and_returns_to_unknown_when_provider_is_still_uncertain()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRequest(PaymentMethodKind.Card);
            var attemptGuid = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await attempts.CreateAsync(new LocalCardPaymentAttempt(
                attemptGuid, null, "LINKLY-UNKNOWN", "Linkly", "Production", "LocalIp", "P", request.Amount,
                LocalCardPaymentAttemptStatus.Recovering, "{}", request.StoreCode, request.DeviceCode, request.CashierId,
                null, null, null, now, now, null, null, "Repayment", request.PaymentGuid));
            await repository.CreateOrGetAsync(CreateOperation(request, LocalInstallmentOperationState.ResultUnknown) with
            {
                TerminalAttemptGuid = attemptGuid.ToString("D"),
                TerminalProcessor = "Linkly"
            });
            var api = new ClaimApi(request, events);
            api.Seed(
                new InstallmentRepaymentClaimCreateRequest(request.PaymentGuid, request.PaymentGuid, request.Amount, request.Method, request.IdempotencyKey!),
                InstallmentRepaymentClaimStatus.Unknown,
                "Linkly",
                attemptGuid.ToString("D"));
            var terminal = new UnknownRecoveryTerminal(events);
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardPaymentAttemptRepository: attempts);

            var recovered = await service.RecoverAsync(Session);

            Assert.False(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(["claim:get", "claim:begin:Linkly", "provider:recover", "claim:resolve:Unknown"], events);
            Assert.Equal(InstallmentRepaymentClaimStatus.Unknown, (await api.GetRepaymentClaimAsync(request.InstallmentGuid, request.PaymentGuid)).Status);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(true, InstallmentRepaymentClaimStatus.Unknown, LocalInstallmentOperationState.ResultUnknown, true)]
    [InlineData(false, InstallmentRepaymentClaimStatus.Declined, LocalInstallmentOperationState.Failed, false)]
    public async Task Voucher_unknown_and_declined_resolve_claim_without_commit(
        bool resultUnknown,
        InstallmentRepaymentClaimStatus expectedClaimStatus,
        LocalInstallmentOperationState expectedLocalStatus,
        bool requiresReview)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Voucher) with { Reference = "VIP003" };
            var api = new ClaimApi(request, events);
            var voucher = new RecordingVoucherTenderClient(events, new PaymentAuthorizationResult(
                false,
                Message: resultUnknown ? "timeout" : "declined",
                ResultUnknown: resultUnknown));
            var service = new InstallmentOperationService(
                repository,
                api,
                new RecordingTerminal(events, new PaymentAuthorizationResult(false)),
                voucher);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);

            Assert.False(result.Succeeded);
            Assert.Equal(requiresReview, result.RequiresReview);
            Assert.Equal(
                ["claim:create", "claim:begin:Voucher", "provider:voucher", $"claim:resolve:{(resultUnknown ? "Unknown" : "Declined")}"],
                events);
            Assert.Equal(expectedClaimStatus, api.Status);
            Assert.Equal(expectedLocalStatus, (await repository.GetAsync(request.PaymentGuid))!.State);
            Assert.Equal(0, api.CommitCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(true, InstallmentRepaymentClaimStatus.Declined, LocalInstallmentOperationState.Failed)]
    [InlineData(false, InstallmentRepaymentClaimStatus.Unknown, LocalInstallmentOperationState.ResultUnknown)]
    public async Task Voucher_partial_lock_must_release_before_claim_can_be_declined(
        bool releaseSucceeded,
        InstallmentRepaymentClaimStatus expectedClaimStatus,
        LocalInstallmentOperationState expectedLocalStatus)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Voucher) with { Reference = "VIP004" };
            var api = new ClaimApi(request, events);
            var voucher = new RecordingVoucherTenderClient(events, new PaymentAuthorizationResult(
                true,
                "VOUCHER:VIP004:LOCK-PARTIAL",
                AuthorizedAmount: request.Amount - 1m))
            {
                ReleaseResult = releaseSucceeded
            };
            var service = new InstallmentOperationService(
                repository,
                api,
                new RecordingTerminal(events, new PaymentAuthorizationResult(false)),
                voucher);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);

            Assert.False(result.Succeeded);
            Assert.Equal(!releaseSucceeded, result.RequiresReview);
            Assert.Equal(1, voucher.RedeemCalls);
            Assert.Equal(1, voucher.ReleaseCalls);
            Assert.Equal(expectedClaimStatus, api.Status);
            Assert.Equal(expectedLocalStatus, (await repository.GetAsync(request.PaymentGuid))!.State);
            Assert.Equal(0, api.CommitCalls);
            Assert.Equal(
                ["claim:create", "claim:begin:Voucher", "provider:voucher", "provider:voucher-release", $"claim:resolve:{(releaseSucceeded ? "Declined" : "Unknown")}"],
                events);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Commit_response_loss_recovers_committed_claim_without_second_commit_or_provider()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Cash);
            var api = new ClaimApi(request, events) { LoseFirstCommitResponse = true };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(false));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var initial = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);
            var recovered = await service.RecoverAsync(Session);

            Assert.False(initial.Succeeded);
            Assert.True(initial.RequiresReview);
            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(1, api.CommitCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(InstallmentRepaymentClaimStatus.Committed, api.Status);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Supported_claims_work_while_not_required_but_unsupported_capability_stops_before_provider()
    {
        var supportedPath = CreateTempDatabasePath();
        var unsupportedPath = CreateTempDatabasePath();
        try
        {
            var supportedEvents = new List<string>();
            var supportedRequest = CreateRequest(PaymentMethodKind.Cash);
            var supportedApi = new ClaimApi(supportedRequest, supportedEvents);
            var supported = new InstallmentOperationService(
                await CreateRepositoryAsync(supportedPath),
                supportedApi,
                new RecordingTerminal(supportedEvents, new PaymentAuthorizationResult(false)),
                new NoopVoucherTenderClient());

            var supportedResult = await supported.ExecuteRepaymentAsync(Session, supportedRequest, authorizeCard: false);

            Assert.True(supportedResult.Succeeded);
            Assert.False(supportedApi.Capabilities.RepaymentClaimsRequired);

            var unsupportedEvents = new List<string>();
            var unsupportedRequest = CreateRequest(PaymentMethodKind.Card);
            var unsupportedApi = new ClaimApi(unsupportedRequest, unsupportedEvents)
            {
                Capabilities = new InstallmentRepaymentCapabilitiesResponse(false, false, false, 120)
            };
            var unsupportedTerminal = new RecordingTerminal(unsupportedEvents, new PaymentAuthorizationResult(true, AuthorizedAmount: unsupportedRequest.Amount));
            var unsupported = new InstallmentOperationService(
                await CreateRepositoryAsync(unsupportedPath),
                unsupportedApi,
                unsupportedTerminal,
                new NoopVoucherTenderClient());

            var unsupportedResult = await unsupported.ExecuteRepaymentAsync(Session, unsupportedRequest, authorizeCard: true);

            Assert.False(unsupportedResult.Succeeded);
            Assert.True(unsupportedResult.RequiresReview);
            Assert.Empty(unsupportedEvents);
            Assert.Equal(0, unsupportedTerminal.AuthorizeCalls);
            Assert.Equal(0, unsupportedApi.LegacyAppendCalls);
        }
        finally
        {
            DeleteTempDatabase(supportedPath);
            DeleteTempDatabase(unsupportedPath);
        }
    }

    [Fact]
    public async Task Card_begin_response_loss_binds_persisted_attempt_and_recovers_without_new_authorization()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRequest(PaymentMethodKind.Card);
            var api = new ClaimApi(request, events) { LoseFirstBeginResponse = true };
            var terminal = new RecoveringTerminal(events, new PaymentAuthorizationResult(
                true,
                "ANZ:RECOVERED",
                AuthorizedAmount: request.Amount,
                CardTransactions: [CreateCardTransaction(request.Amount)],
                Processor: "Linkly"));
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var initial = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);
            var recovered = await service.RecoverAsync(Session);

            Assert.False(initial.Succeeded);
            Assert.True(initial.RequiresReview);
            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(1, terminal.RecoveryCalls);
            Assert.Equal(InstallmentRepaymentClaimStatus.Committed, api.Status);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
            Assert.Equal(["claim:create", "claim:begin:Linkly", "claim:get", "provider:recover", "claim:commit"], events);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(false, InstallmentRepaymentClaimStatus.Declined, LocalInstallmentOperationState.Failed)]
    [InlineData(true, InstallmentRepaymentClaimStatus.Unknown, LocalInstallmentOperationState.ResultUnknown)]
    public async Task Voucher_begin_response_loss_calls_provider_once_then_reaches_safe_terminal_state(
        bool resultUnknown,
        InstallmentRepaymentClaimStatus expectedClaimStatus,
        LocalInstallmentOperationState expectedLocalStatus)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Voucher) with { Reference = "VIP-CRASH" };
            var api = new ClaimApi(request, events) { LoseFirstBeginResponse = true };
            var voucher = new RecordingVoucherTenderClient(events, new PaymentAuthorizationResult(
                false,
                Message: resultUnknown ? "timeout" : "declined",
                ResultUnknown: resultUnknown));
            var service = new InstallmentOperationService(
                repository,
                api,
                new RecordingTerminal(events, new PaymentAuthorizationResult(false)),
                voucher);

            var initial = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);
            var recovered = await service.RecoverAsync(Session);

            Assert.False(initial.Succeeded);
            Assert.True(initial.RequiresReview);
            Assert.False(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(1, voucher.RedeemCalls);
            Assert.Equal(expectedClaimStatus, api.Status);
            Assert.Equal(expectedLocalStatus, (await repository.GetAsync(request.PaymentGuid))!.State);
            Assert.Equal(0, api.CommitCalls);
            Assert.Equal(
                ["claim:create", "claim:begin:Voucher", "claim:get", "claim:begin:Voucher", "provider:voucher", $"claim:resolve:{(resultUnknown ? "Unknown" : "Declined")}"],
                events);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cash_begin_response_loss_recovers_to_commit_without_external_provider()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRequest(PaymentMethodKind.Cash);
            var api = new ClaimApi(request, events) { LoseFirstBeginResponse = true };
            var terminal = new RecordingTerminal(events, new PaymentAuthorizationResult(false));
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var initial = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: false);
            var recovered = await service.RecoverAsync(Session);

            Assert.False(initial.Succeeded);
            Assert.True(initial.RequiresReview);
            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(InstallmentRepaymentClaimStatus.Committed, api.Status);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
            Assert.Equal(["claim:create", "claim:begin:Cash", "claim:get", "claim:commit"], events);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    private static readonly PosSessionState Session = new("HB POS", "S001", "Main", "POS-01", "C001", "Alice", true, 0);

    private static InstallmentAppendPaymentRequest CreateRequest(PaymentMethodKind method) => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Guid.NewGuid(),
        Session.StoreCode,
        Session.DeviceCode,
        Session.CashierId,
        Session.CashierName,
        40m,
        method,
        null,
        null,
        null,
        $"claim-test:{Guid.NewGuid():D}");

    private static LocalInstallmentOperation CreateOperation(InstallmentAppendPaymentRequest request, LocalInstallmentOperationState state)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalInstallmentOperation(
            request.PaymentGuid,
            LocalInstallmentOperationKind.Repayment,
            request.InstallmentGuid,
            request.PaymentGuid,
            request.StoreCode,
            request.DeviceCode,
            request.CashierId,
            request.IdempotencyKey!,
            JsonSerializer.Serialize(request),
            state,
            null,
            null,
            null,
            null,
            now,
            now);
    }

    private static InstallmentAppendPaymentResponse CreateResponse(InstallmentAppendPaymentRequest request)
    {
        var payment = new InstallmentPaymentDto(
            request.PaymentGuid,
            request.Method,
            request.Amount,
            request.Reference,
            InstallmentPaymentStatus.Recorded,
            DateTimeOffset.UtcNow,
            Session.CashierId,
            Session.DeviceCode,
            request.CardTransactions,
            request.IdempotencyKey,
            CashierName: Session.CashierName);
        var details = new InstallmentDetailsDto(
            request.InstallmentGuid,
            "IP-CLAIM-1",
            Session.StoreCode,
            Session.DeviceCode,
            Session.CashierId,
            Session.CashierName,
            "Customer",
            "0400000000",
            DateTimeOffset.UtcNow,
            100m,
            20m,
            20m,
            60m,
            40m,
            InstallmentStatus.Active,
            [],
            [payment],
            null);
        return new InstallmentAppendPaymentResponse(request.InstallmentGuid, request.PaymentGuid, 60m, 40m, InstallmentStatus.Active, details);
    }

    private static CardTransactionDto CreateCardTransaction(decimal amount, string txnRef = "CARD-1") =>
        new("Linkly", txnRef, "AUTH-1", "VISA", 4, "1234", "MID-1", "00", "APPROVED", "RRN-1", DateTimeOffset.UtcNow, amount, "receipt");

    private static async Task<LocalInstallmentOperationRepository> CreateRepositoryAsync(string path)
    {
        var store = new LocalSqliteStore(path);
        await new LocalSchemaService(store).InitializeAsync();
        return new LocalInstallmentOperationRepository(store);
    }

    private static string CreateTempDatabasePath() => Path.Combine(Path.GetTempPath(), $"hbpos-installment-claim-client-{Guid.NewGuid():N}.db");

    private static void DeleteTempDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private sealed class ClaimApi(InstallmentAppendPaymentRequest request, List<string> events) : IInstallmentApiClient
    {
        private InstallmentRepaymentClaimStatus _status = InstallmentRepaymentClaimStatus.Prepared;
        private string? _provider;
        private string? _providerAttemptId;
        private InstallmentAppendPaymentResponse? _commit;
        private bool _beginResponseLost;
        private bool _commitResponseLost;
        public Exception? CreateException { get; init; }
        public Exception? GetException { get; set; }
        public bool LoseFirstBeginResponse { get; init; }
        public bool LoseFirstCommitResponse { get; init; }
        public Exception? BeginException { get; init; }
        public Exception? ResolveException { get; set; }
        // 默认按「三个刷卡通道都已放开」的服务端：这些用例验证的是放开之后的续付流程。
        public InstallmentRepaymentCapabilitiesResponse Capabilities { get; init; } = new(
            true,
            false,
            true,
            120,
            CardRepaymentSupported: true,
            CardRepaymentProviders: ["square", "linkly", "zeller", "manual"]);
        public InstallmentRepaymentClaimStatus Status => _status;
        public string? ProviderAttemptId => _providerAttemptId;
        public int CommitCalls { get; private set; }
        public int LegacyAppendCalls { get; private set; }
        public InstallmentRepaymentClaimCreateRequest? LastCreate { get; private set; }
        public InstallmentRepaymentClaimCommitRequest? LastCommittedRequest { get; private set; }

        public Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Capabilities);

        public Task<InstallmentRepaymentClaimDto> CreateRepaymentClaimAsync(Guid installmentGuid, InstallmentRepaymentClaimCreateRequest claimRequest, CancellationToken cancellationToken = default)
        {
            events.Add("claim:create");
            if (CreateException is not null) return Task.FromException<InstallmentRepaymentClaimDto>(CreateException);
            LastCreate = claimRequest;
            return Task.FromResult(CreateClaim(claimRequest, _status));
        }

        public Task<InstallmentRepaymentClaimDto> BeginRepaymentProviderAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimBeginProviderRequest beginRequest, CancellationToken cancellationToken = default)
        {
            events.Add($"claim:begin:{beginRequest.Provider}");
            if (BeginException is not null) return Task.FromException<InstallmentRepaymentClaimDto>(BeginException);
            _status = InstallmentRepaymentClaimStatus.ProviderPending;
            _provider = beginRequest.Provider;
            _providerAttemptId = beginRequest.ProviderAttemptId;
            if (LoseFirstBeginResponse && !_beginResponseLost)
            {
                _beginResponseLost = true;
                return Task.FromException<InstallmentRepaymentClaimDto>(new HttpRequestException("begin response lost"));
            }
            return Task.FromResult(CreateClaim(LastCreate!, _status, _provider, _providerAttemptId));
        }

        public Task<InstallmentRepaymentClaimDto> GetRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default)
        {
            events.Add("claim:get");
            if (GetException is not null)
            {
                var exception = GetException;
                GetException = null;
                return Task.FromException<InstallmentRepaymentClaimDto>(exception);
            }
            return Task.FromResult(CreateClaim(LastCreate!, _status, _provider, _providerAttemptId));
        }

        public void Seed(InstallmentRepaymentClaimCreateRequest claim, InstallmentRepaymentClaimStatus status, string provider, string providerAttemptId)
        {
            LastCreate = claim;
            _status = status;
            _provider = provider;
            _providerAttemptId = providerAttemptId;
        }

        public Task<InstallmentRepaymentClaimDto> ResolveRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimResolveRequest resolveRequest, CancellationToken cancellationToken = default)
        {
            if (ResolveException is not null) return Task.FromException<InstallmentRepaymentClaimDto>(ResolveException);
            _status = resolveRequest.Outcome switch
            {
                InstallmentRepaymentClaimResolveOutcome.Released => InstallmentRepaymentClaimStatus.Released,
                InstallmentRepaymentClaimResolveOutcome.Declined => InstallmentRepaymentClaimStatus.Declined,
                _ => InstallmentRepaymentClaimStatus.Unknown
            };
            events.Add($"claim:resolve:{resolveRequest.Outcome}");
            return Task.FromResult(CreateClaim(LastCreate!, _status, _provider, _providerAttemptId));
        }

        public Task<InstallmentRepaymentClaimDto> CommitRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimCommitRequest commitRequest, CancellationToken cancellationToken = default)
        {
            events.Add("claim:commit");
            CommitCalls++;
            LastCommittedRequest = commitRequest;
            _status = InstallmentRepaymentClaimStatus.Committed;
            var committedRequest = request with
            {
                Reference = commitRequest.Reference,
                ReservationToken = commitRequest.ReservationToken,
                CardTransactions = commitRequest.CardTransactions
            };
            _commit = CreateResponse(committedRequest);
            if (LoseFirstCommitResponse && !_commitResponseLost)
            {
                _commitResponseLost = true;
                return Task.FromException<InstallmentRepaymentClaimDto>(new HttpRequestException("commit response lost"));
            }
            return Task.FromResult(CreateClaim(LastCreate!, _status, _provider, _providerAttemptId));
        }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            LegacyAppendCalls++;
            throw new InvalidOperationException("Claim-aware repayment must not call the legacy append endpoint.");
        }
        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private InstallmentRepaymentClaimDto CreateClaim(
            InstallmentRepaymentClaimCreateRequest value,
            InstallmentRepaymentClaimStatus status,
            string? provider = null,
            string? providerAttemptId = null) =>
            new(request.InstallmentGuid, value.OperationGuid, value.PaymentGuid, value.Amount, value.Method, value.IdempotencyKey,
                status, provider, providerAttemptId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, _commit);
    }

    private sealed class RecordingTerminal(
        List<string> events,
        PaymentAuthorizationResult result,
        Func<decimal, PaymentAuthorizationResult>? resultFactory = null) : ICardTerminalClient
    {
        public int AuthorizeCalls { get; private set; }
        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            events.Add("provider:card");
            return Task.FromResult(resultFactory?.Invoke(amount) ?? result);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnknownRecoveryTerminal(List<string> events) : ICardTerminalClient, IInstallmentTerminalRecoveryClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Recovery must not create a new authorization.");

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RecoverLinklyAsync(LocalCardPaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default)
        {
            events.Add("provider:recover");
            return Task.FromResult(new PaymentAuthorizationResult(false, ResultUnknown: true));
        }

        public Task<PaymentAuthorizationResult> RecoverSquareAsync(LocalSquarePaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecoveringTerminal(List<string> events, PaymentAuthorizationResult recovery) : ICardTerminalClient, IInstallmentTerminalRecoveryClient
    {
        public int AuthorizeCalls { get; private set; }
        public int RecoveryCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            throw new InvalidOperationException("begin 响应丢失后的恢复不得新建授权。");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RecoverLinklyAsync(LocalCardPaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            events.Add("provider:recover");
            return Task.FromResult(recovery);
        }

        public Task<PaymentAuthorizationResult> RecoverSquareAsync(LocalSquarePaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopVoucherTenderClient : IVoucherTenderClient
    {
        public Task<PaymentAuthorizationResult> RedeemAsync(decimal amount, PosSessionState session, string? voucherCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PaymentAuthorizationResult> IssueRefundAsync(decimal amount, PosSessionState session, string orderReference, string idempotencyKey, string? reason = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ReleaseAsync(PosSessionState session, string voucherCode, string reservationToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingVoucherTenderClient(List<string> events, PaymentAuthorizationResult result) : IVoucherTenderClient
    {
        public int RedeemCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public bool ReleaseResult { get; init; } = true;

        public Task<PaymentAuthorizationResult> RedeemAsync(decimal amount, PosSessionState session, string? voucherCode, CancellationToken cancellationToken = default)
        {
            RedeemCalls++;
            events.Add("provider:voucher");
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(decimal amount, PosSessionState session, string orderReference, string idempotencyKey, string? reason = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ReleaseAsync(PosSessionState session, string voucherCode, string reservationToken, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            events.Add("provider:voucher-release");
            return Task.FromResult(ReleaseResult);
        }
    }
}

/// <summary>旧服务测试只录制 append；此适配器把它包在测试专用 claim 状态机后，生产代码不会回退旧端点。</summary>
internal sealed class ClaimAwareInstallmentApiTestAdapter(
    IInstallmentApiClient inner,
    InstallmentAppendPaymentRequest repayment,
    RepaymentClaimTestState state) : IInstallmentApiClient
{
    // 按「三个刷卡通道都已放开」的服务端：共用这个适配器的用例验证的是放开之后的续付流程。
    public Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstallmentRepaymentCapabilitiesResponse(
            true,
            false,
            true,
            120,
            CardRepaymentSupported: true,
            CardRepaymentProviders: ["square", "linkly", "zeller"]));

    public Task<InstallmentRepaymentClaimDto> CreateRepaymentClaimAsync(Guid installmentGuid, InstallmentRepaymentClaimCreateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Create(request, repayment));

    public Task<InstallmentRepaymentClaimDto> BeginRepaymentProviderAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimBeginProviderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Begin(operationGuid, request));

    public Task<InstallmentRepaymentClaimDto> GetRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Get(operationGuid));

    public Task<InstallmentRepaymentClaimDto> ResolveRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimResolveRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Resolve(operationGuid, request.Outcome));

    public async Task<InstallmentRepaymentClaimDto> CommitRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimCommitRequest request, CancellationToken cancellationToken = default)
    {
        var append = repayment with
        {
            Reference = request.Reference,
            ReservationToken = request.ReservationToken,
            CardTransactions = request.CardTransactions
        };
        var response = await inner.AppendPaymentAsync(append, cancellationToken);
        return state.Commit(operationGuid, response);
    }

    public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) => inner.CreateAsync(request, cancellationToken);
    public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("测试适配器也禁止生产流程直接调用旧 append。");
    public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) => inner.ConfirmPickupAsync(request, cancellationToken);
    public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default) => inner.CancelAsync(request, cancellationToken);
    public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) => inner.VoidAsync(request, cancellationToken);
}

internal sealed class RepaymentClaimTestState
{
    private readonly object _gate = new();
    private InstallmentRepaymentClaimCreateRequest? _request;
    private InstallmentAppendPaymentRequest? _repayment;
    private InstallmentRepaymentClaimStatus _status = InstallmentRepaymentClaimStatus.Prepared;
    private string? _provider;
    private string? _providerAttemptId;
    private InstallmentAppendPaymentResponse? _commit;

    public string? LastBeginProvider { get; private set; }
    public string? LastBeginProviderAttemptId { get; private set; }

    public void Seed(InstallmentAppendPaymentRequest repayment, InstallmentRepaymentClaimStatus status, string provider, string providerAttemptId)
    {
        lock (_gate)
        {
            _repayment = repayment;
            _request = new InstallmentRepaymentClaimCreateRequest(repayment.PaymentGuid, repayment.PaymentGuid, repayment.Amount, repayment.Method, repayment.IdempotencyKey!);
            _status = status;
            _provider = provider;
            _providerAttemptId = providerAttemptId;
        }
    }

    public InstallmentRepaymentClaimDto Create(InstallmentRepaymentClaimCreateRequest request, InstallmentAppendPaymentRequest repayment)
    {
        lock (_gate)
        {
            _request ??= request;
            _repayment ??= repayment;
            return Snapshot();
        }
    }

    public InstallmentRepaymentClaimDto Begin(Guid operationGuid, InstallmentRepaymentClaimBeginProviderRequest request)
    {
        lock (_gate)
        {
            EnsureOperation(operationGuid);
            LastBeginProvider = request.Provider;
            LastBeginProviderAttemptId = request.ProviderAttemptId;
            if (_status == InstallmentRepaymentClaimStatus.Unknown || _status == InstallmentRepaymentClaimStatus.Prepared)
            {
                _status = InstallmentRepaymentClaimStatus.ProviderPending;
            }
            _provider ??= request.Provider;
            _providerAttemptId ??= request.ProviderAttemptId;
            return Snapshot();
        }
    }

    public InstallmentRepaymentClaimDto Get(Guid operationGuid)
    {
        lock (_gate)
        {
            EnsureOperation(operationGuid);
            return Snapshot();
        }
    }

    public InstallmentRepaymentClaimDto Resolve(Guid operationGuid, InstallmentRepaymentClaimResolveOutcome outcome)
    {
        lock (_gate)
        {
            EnsureOperation(operationGuid);
            _status = outcome switch
            {
                InstallmentRepaymentClaimResolveOutcome.Released => InstallmentRepaymentClaimStatus.Released,
                InstallmentRepaymentClaimResolveOutcome.Declined => InstallmentRepaymentClaimStatus.Declined,
                _ => InstallmentRepaymentClaimStatus.Unknown
            };
            return Snapshot();
        }
    }

    public InstallmentRepaymentClaimDto Commit(Guid operationGuid, InstallmentAppendPaymentResponse response)
    {
        lock (_gate)
        {
            EnsureOperation(operationGuid);
            _status = InstallmentRepaymentClaimStatus.Committed;
            _commit = response;
            return Snapshot();
        }
    }

    private void EnsureOperation(Guid operationGuid)
    {
        if (_request?.OperationGuid != operationGuid)
        {
            throw new InvalidOperationException("测试 claim operation 不匹配。");
        }
    }

    private InstallmentRepaymentClaimDto Snapshot() => new(
        _repayment!.InstallmentGuid,
        _request!.OperationGuid,
        _request.PaymentGuid,
        _request.Amount,
        _request.Method,
        _request.IdempotencyKey,
        _status,
        _provider,
        _providerAttemptId,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        _commit,
        AlreadyExists: true);
}
