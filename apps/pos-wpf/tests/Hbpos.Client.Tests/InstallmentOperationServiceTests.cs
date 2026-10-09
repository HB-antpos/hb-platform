using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

// 部分用例截获 ConsoleLog 中心日志（全局静态），因此整个类加入 ConsoleLog 全局状态集合串行执行。
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class InstallmentOperationServiceTests
{
    [Fact]
    public async Task Pickup_api_failure_marked_result_unknown_writes_traceable_warning()
    {
        var path = CreateTempDatabasePath();
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var installmentGuid = Guid.NewGuid();
            var request = new InstallmentConfirmPickupRequest(
                installmentGuid,
                Session.StoreCode,
                Session.DeviceCode,
                Session.CashierId,
                Session.CashierName,
                DateTimeOffset.UtcNow,
                OperationGuid: installmentGuid,
                IdempotencyKey: $"{installmentGuid:D}:pickup");
            var api = new RecordingInstallmentApi
            {
                PickupException = new CatalogApiException(
                    "gateway failed",
                    System.Net.HttpStatusCode.BadGateway,
                    "INSTALLMENT_UPSTREAM_FAILED")
            };
            var service = CreateService(repository, api, new CountingTerminal(approve: true));

            var result = await service.ExecutePickupAsync(Session, request);

            Assert.True(result.RequiresReview);
            var unknown = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, unknown.State);
            // 此前 MarkApiUnknownAsync 只写本地库；现在必须有一条以 operationGuid 为 TraceId 的 Warning。
            var entry = Assert.Single(sink.Entries, item =>
                item.Category == "Installment" && item.TraceId == unknown.OperationGuid.ToString("D"));
            Assert.Equal("Warning", entry.Level);
            Assert.Equal(502, entry.StatusCode);
            Assert.Equal("INSTALLMENT_UPSTREAM_FAILED", entry.Properties!["errorCode"]);
            Assert.Equal("pickup", entry.Properties["operation"]);
            Assert.Contains("ResultUnknown", entry.Message);
            Assert.Equal(nameof(CatalogApiException), entry.ExceptionType);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Create_is_blocked_when_terminal_has_another_recoverable_create()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var existingRequest = CreateInstallmentRequest(Guid.NewGuid());
            var now = DateTimeOffset.UtcNow;
            await repository.CreateOrGetAsync(new LocalInstallmentOperation(
                existingRequest.DownPayment.PaymentGuid,
                LocalInstallmentOperationKind.Create,
                existingRequest.InstallmentGuid,
                existingRequest.DownPayment.PaymentGuid,
                existingRequest.StoreCode,
                existingRequest.DeviceCode,
                existingRequest.CashierId,
                existingRequest.DownPayment.IdempotencyKey!,
                JsonSerializer.Serialize(existingRequest),
                LocalInstallmentOperationState.ResultUnknown,
                null,
                null,
                null,
                "API result unknown",
                now,
                now));
            var terminal = new CountingTerminal(approve: true);
            var service = CreateService(repository, new RecordingInstallmentApi(), terminal);
            var nextRequest = CreateInstallmentRequest(Guid.NewGuid());

            var result = await service.ExecuteCreateAsync(Session, nextRequest, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(PaymentMethodKind.Cash, null)]
    [InlineData(PaymentMethodKind.Voucher, null)]
    [InlineData(PaymentMethodKind.Card, "Linkly")]
    [InlineData(PaymentMethodKind.Card, "Square")]
    public async Task Repayment_persists_action_before_claim_then_begins_provider_before_provider_and_commit(
        PaymentMethodKind method,
        string? cardProcessor)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var events = new List<string>();
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var linklyAttempts = new LocalCardPaymentAttemptRepository(store);
            var squareAttempts = new LocalSquarePaymentAttemptRepository(store);
            var request = CreateRepaymentRequest() with
            {
                Method = method,
                Reference = method == PaymentMethodKind.Voucher ? "VOUCHER-1" : null
            };
            var legacyApi = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var claimApi = new DurableActionOrderingApi(
                new ClaimAwareInstallmentApiTestAdapter(legacyApi, request, claimState),
                repository,
                events);
            var terminal = new RepaymentOrderingTerminal(cardProcessor, events);
            var voucher = new RepaymentOrderingVoucherClient(events);
            var processor = cardProcessor switch
            {
                "Linkly" => CardProcessorKind.Linkly,
                "Square" => CardProcessorKind.Square,
                _ => CardProcessorKind.None
            };
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = processor,
                LinklyConnectionMode = LinklyConnectionMode.CloudDirectSync,
                SquareDeviceId = "device:TEST-DEVICE",
                SquareLocationId = "TEST-LOCATION"
            };
            var service = new InstallmentOperationService(
                repository,
                claimApi,
                terminal,
                voucher,
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: linklyAttempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor(),
                squarePaymentAttemptRepository: squareAttempts,
                squarePaymentAttemptContextAccessor: new SquarePaymentAttemptContextAccessor());

            var result = await service.ExecuteRepaymentAsync(
                Session,
                request,
                authorizeCard: method == PaymentMethodKind.Card);

            Assert.True(result.Succeeded, result.Message);
            var provider = cardProcessor ?? method.ToString();
            var expected = new List<string>
            {
                "action:persisted",
                "claim:create",
                $"claim:begin:{provider}"
            };
            if (method != PaymentMethodKind.Cash)
            {
                expected.Add($"provider:{provider}");
            }
            expected.Add("claim:commit");
            Assert.Equal(expected, events);
            Assert.Equal(method == PaymentMethodKind.Card ? 1 : 0, terminal.AuthorizeCalls);
            Assert.Equal(method == PaymentMethodKind.Voucher ? 1 : 0, voucher.RedeemCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(LinklyConnectionMode.LocalIp, LinklyConnectionMode.CloudBackendAsync)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync, LinklyConnectionMode.LocalIp)]
    public async Task Installment_purchase_uses_first_settings_snapshot_for_route_and_persisted_identity(
        LinklyConnectionMode firstMode,
        LinklyConnectionMode laterMode)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var firstSettings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = firstMode
            };
            var laterSettings = firstSettings with { LinklyConnectionMode = laterMode };
            var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(firstSettings, laterSettings);
            var routedLinkly = new TxnRefSnapshotLinklyTerminalClient(context);
            using var httpClient = new HttpClient();
            var configuredTerminal = new ConfiguredCardTerminalClient(
                settingsProvider,
                httpClient,
                routedLinkly,
                linklyPaymentAttemptContextAccessor: context);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                configuredTerminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: settingsProvider,
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.NotNull(routedLinkly.LastAttemptGuid);
            var attempt = await attempts.GetAttemptAsync(routedLinkly.LastAttemptGuid.Value);
            Assert.NotNull(attempt);
            Assert.Equal(CardTerminalSettings.FormatLinklyConnectionMode(firstMode), attempt.ConnectionMode);
            Assert.Equal(firstMode, routedLinkly.LastMode);
            Assert.Equal(firstMode == LinklyConnectionMode.LocalIp ? 1 : 0, routedLinkly.LocalPurchaseCalls);
            Assert.Equal(firstMode == LinklyConnectionMode.CloudBackendAsync ? 1 : 0, routedLinkly.CloudPurchaseCalls);
            Assert.Equal(attempt.TxnRef, routedLinkly.LastTxnRef);
            Assert.False(string.IsNullOrWhiteSpace(attempt.TxnRef));
            Assert.Equal(1, api.AppendCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Local_ip_installment_purchase_persists_stable_txn_ref_before_terminal_with_long_device_code()
    {
        var path = CreateTempDatabasePath();
        try
        {
            const string deviceCode = "POS-DEVICE-CODE-THAT-IS-DELIBERATELY-LONG-0001";
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest() with { DeviceCode = deviceCode };
            var session = Session with { DeviceCode = deviceCode };
            var terminal = new PersistedLinklyPurchaseTerminal(attempts, context);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context);

            var result = await service.ExecuteRepaymentAsync(session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.NotNull(terminal.AttemptGuid);
            var persisted = await attempts.GetAttemptAsync(terminal.AttemptGuid.Value);
            Assert.NotNull(persisted);
            AssertLocalTxnRef('P', terminal.SubmittedTxnRef);
            Assert.Equal(persisted.TxnRef, terminal.SubmittedTxnRef);
            Assert.Equal(
                LinklyLocalTxnRef.Create('P', persisted.AttemptGuid.ToString("D")),
                persisted.TxnRef);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Restarted_local_ip_installment_purchase_reuses_persisted_attempt_txn_ref()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var now = DateTimeOffset.UtcNow;
            var attemptGuid = Guid.Parse("f588a502-9129-e8ad-8627-3d9e1b2b892a");
            const string persistedTxnRef = "P0123456789ABCDE";
            await attempts.CreateAsync(new LocalCardPaymentAttempt(
                attemptGuid,
                null,
                persistedTxnRef,
                CardProcessorKind.Linkly.ToString(),
                CardTerminalEnvironment.Sandbox.ToString(),
                LinklyConnectionMode.LocalIp.ToString(),
                "P",
                request.Amount,
                LocalCardPaymentAttemptStatus.Pending,
                JsonSerializer.Serialize(request),
                request.StoreCode,
                request.DeviceCode,
                request.CashierId,
                null,
                null,
                null,
                now,
                now,
                null,
                null,
                "Repayment",
                request.PaymentGuid));
            var terminal = new PersistedLinklyPurchaseTerminal(attempts, context);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var restartedService = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context);

            var result = await restartedService.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(attemptGuid, terminal.AttemptGuid);
            AssertLocalTxnRef('P', terminal.SubmittedTxnRef);
            Assert.Equal(persistedTxnRef, terminal.SubmittedTxnRef);
            Assert.Equal(persistedTxnRef, (await attempts.GetAttemptAsync(attemptGuid))!.TxnRef);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(LinklyConnectionMode.LocalIp, LinklyConnectionMode.CloudBackendAsync)]
    [InlineData(LinklyConnectionMode.CloudBackendAsync, LinklyConnectionMode.LocalIp)]
    public async Task Restarted_installment_purchase_with_connection_mode_mismatch_stays_locked_without_terminal_call(
        LinklyConnectionMode currentMode,
        LinklyConnectionMode persistedMode)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var persistedAttempt = CreatePersistedInstallmentLinklyAttempt(
                request,
                persistedMode,
                "P",
                persistedMode == LinklyConnectionMode.LocalIp ? "P0123456789ABCDE" : null);
            await attempts.CreateAsync(persistedAttempt);
            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = currentMode
                }),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(0, api.AppendCalls);
            Assert.Null(claimState.LastBeginProvider);
            Assert.Equal(persistedAttempt, await attempts.GetAttemptAsync(persistedAttempt.AttemptGuid));
            var operation = Assert.IsType<LocalInstallmentOperation>(await repository.GetAsync(request.PaymentGuid));
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, operation.State);
            Assert.Null(operation.TerminalAttemptGuid);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData("R", "P0123456789ABCDE")]
    [InlineData("P", "P0123456789ABCDEF")]
    [InlineData("P", "P0123\u001f")]
    [InlineData("P", "PCAFÉ")]
    [InlineData("P", "ANZ:")]
    [InlineData("P", "ANZ:   ")]
    public async Task Restarted_local_ip_installment_purchase_with_invalid_type_or_reference_stays_locked_without_terminal_call(
        string txnType,
        string txnRef)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var persistedAttempt = CreatePersistedInstallmentLinklyAttempt(
                request,
                LinklyConnectionMode.LocalIp,
                txnType,
                txnRef);
            await attempts.CreateAsync(persistedAttempt);
            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(0, api.AppendCalls);
            Assert.Null(claimState.LastBeginProvider);
            Assert.Equal(persistedAttempt, await attempts.GetAttemptAsync(persistedAttempt.AttemptGuid));
            var operation = Assert.IsType<LocalInstallmentOperation>(await repository.GetAsync(request.PaymentGuid));
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, operation.State);
            Assert.Null(operation.TerminalAttemptGuid);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData("P")]
    [InlineData("P-OLD#1")]
    [InlineData("P0123456789ABCDE")]
    public async Task Restarted_local_ip_installment_purchase_accepts_printable_historical_persisted_reference(string persistedTxnRef)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var persistedAttempt = CreatePersistedInstallmentLinklyAttempt(
                request,
                LinklyConnectionMode.LocalIp,
                "P",
                persistedTxnRef);
            await attempts.CreateAsync(persistedAttempt);
            var terminal = new PersistedLinklyPurchaseTerminal(attempts, context);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
                {
                    Processor = CardProcessorKind.Linkly,
                    LinklyConnectionMode = LinklyConnectionMode.LocalIp
                }),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(persistedTxnRef, terminal.SubmittedTxnRef);
            Assert.Equal(persistedTxnRef, (await attempts.GetAttemptAsync(persistedAttempt.AttemptGuid))!.TxnRef);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Repayment_api_cancellation_recovers_by_replaying_only_the_original_api_request()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var claimState = new RepaymentClaimTestState();
            var firstTerminal = new CountingTerminal(approve: true);
            var firstApi = new RecordingInstallmentApi { AppendException = new OperationCanceledException() };
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.CloudDirectSync
            };
            var firstService = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(firstApi, request, claimState),
                firstTerminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var initial = await firstService.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(initial.Succeeded);
            Assert.True(initial.RequiresReview);
            Assert.Equal(1, firstTerminal.AuthorizeCalls);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, operation.State);

            var restartedTerminal = new CountingTerminal(approve: true);
            var restartedApi = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var restartedService = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(restartedApi, request, claimState),
                restartedTerminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var recovered = await restartedService.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(0, restartedTerminal.AuthorizeCalls);
            Assert.Equal(1, restartedApi.AppendCalls);
            Assert.Equal(request.PaymentGuid, restartedApi.LastAppendRequest!.PaymentGuid);
            Assert.Equal(request.IdempotencyKey, restartedApi.LastAppendRequest.IdempotencyKey);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Concurrent_recovery_uses_one_api_claim_and_does_not_repeat_the_terminal()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRepaymentRequest();
            var operation = CreateApprovedRepaymentOperation(request);
            await repository.CreateOrGetAsync(operation);
            var claimState = new RepaymentClaimTestState();
            claimState.Seed(request, InstallmentRepaymentClaimStatus.ProviderPending, "Linkly", operation.TerminalAttemptGuid!);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request), AppendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            var terminal = new CountingTerminal(approve: true);
            var service = CreateService(repository, new ClaimAwareInstallmentApiTestAdapter(api, request, claimState), terminal);

            var firstRecovery = service.RecoverAsync(Session);
            await api.AppendStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var secondRecovery = service.RecoverAsync(Session);
            api.AppendGate.SetResult();
            await Task.WhenAll(firstRecovery, secondRecovery);

            Assert.Equal(1, api.AppendCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(operation.OperationGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Restarted_service_immediately_claims_recent_api_submitting_with_a_new_process_token()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var request = CreateRepaymentRequest();
            var operation = CreateApprovedRepaymentOperation(request);
            await repository.CreateOrGetAsync(operation);
            Assert.True(await repository.TryClaimApiAsync(operation.OperationGuid, "previous-process", false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)));

            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            claimState.Seed(request, InstallmentRepaymentClaimStatus.ProviderPending, "Linkly", operation.TerminalAttemptGuid!);
            var restartedService = CreateService(repository, new ClaimAwareInstallmentApiTestAdapter(api, request, claimState), terminal);

            var recovered = await restartedService.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(1, api.AppendCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(operation.OperationGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Persisted_linkly_approved_attempt_with_wrong_amount_does_not_synthesize_recovery_or_call_api()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attemptRepository = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var attemptGuid = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await attemptRepository.CreateAsync(new LocalCardPaymentAttempt(
                attemptGuid, null, "LINKLY-APPROVED", "Linkly", "Production", "LocalIp", "P", request.Amount + 1m,
                LocalCardPaymentAttemptStatus.Approved, "{}", request.StoreCode, request.DeviceCode, request.CashierId,
                "00", "APPROVED", "LINKLY-APPROVED", now, now, now, null, "Repayment", request.PaymentGuid));
            var operation = CreateApprovedRepaymentOperation(request) with
            {
                State = LocalInstallmentOperationState.ResultUnknown,
                TerminalAttemptGuid = attemptGuid.ToString("D"),
                RequestJson = JsonSerializer.Serialize(request)
            };
            await repository.CreateOrGetAsync(operation);
            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            claimState.Seed(request, InstallmentRepaymentClaimStatus.Unknown, "Linkly", attemptGuid.ToString("D"));
            var service = new InstallmentOperationService(repository, new ClaimAwareInstallmentApiTestAdapter(api, request, claimState), terminal, new NoopVoucherTenderClient(), cardPaymentAttemptRepository: attemptRepository);

            var recovered = await service.RecoverAsync(Session);

            Assert.False(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(0, api.AppendCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unknown_linkly_attempt_queries_remote_approval_then_replays_only_api()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attemptRepository = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var attemptGuid = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await attemptRepository.CreateAsync(new LocalCardPaymentAttempt(
                attemptGuid, null, "LINKLY-UNKNOWN", "Linkly", "Production", "LocalIp", "P", request.Amount,
                LocalCardPaymentAttemptStatus.Recovering, "{}", request.StoreCode, request.DeviceCode, request.CashierId,
                null, null, null, now, now, null, null, "Repayment", request.PaymentGuid));
            await repository.CreateOrGetAsync(CreateApprovedRepaymentOperation(request) with
            {
                State = LocalInstallmentOperationState.ResultUnknown,
                TerminalAttemptGuid = attemptGuid.ToString("D"),
                RequestJson = JsonSerializer.Serialize(request)
            });

            var terminal = new RemoteApprovalTerminal(request.Amount);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            claimState.Seed(request, InstallmentRepaymentClaimStatus.Unknown, "Linkly", attemptGuid.ToString("D"));
            var service = new InstallmentOperationService(repository, new ClaimAwareInstallmentApiTestAdapter(api, request, claimState), terminal, new NoopVoucherTenderClient(), cardPaymentAttemptRepository: attemptRepository);

            var recovered = await service.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(1, terminal.RecoveryCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(1, api.AppendCalls);
            Assert.Equal("ANZ:LINKLY-REMOTE", api.LastAppendRequest!.Reference);
            Assert.Equal("Linkly", claimState.LastBeginProvider);
            Assert.Equal(attemptGuid.ToString("D"), claimState.LastBeginProviderAttemptId);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unknown_square_attempt_queries_remote_approval_with_payment_reference_shape_then_replays_only_api()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalSquarePaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var attemptGuid = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await attempts.CreateAsync(new LocalSquarePaymentAttempt(
                attemptGuid, "checkout-001", "persisted-square-key", "device-001", "location-001", "Production", request.Amount, 4000, "AUD",
                LocalSquarePaymentAttemptStatus.Unknown, "IN_PROGRESS", null, "{}", request.StoreCode, request.DeviceCode, request.CashierId,
                null, null, null, null, now, now, null, null, null, "Repayment", request.PaymentGuid));
            await repository.CreateOrGetAsync(CreateApprovedRepaymentOperation(request) with
            {
                State = LocalInstallmentOperationState.ResultUnknown,
                TerminalAttemptGuid = attemptGuid.ToString("D"),
                RequestJson = JsonSerializer.Serialize(request)
            });

            var terminal = new RemoteSquareApprovalTerminal(request.Amount);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            claimState.Seed(request, InstallmentRepaymentClaimStatus.Unknown, "Square", attemptGuid.ToString("D"));
            var service = new InstallmentOperationService(repository, new ClaimAwareInstallmentApiTestAdapter(api, request, claimState), terminal, new NoopVoucherTenderClient(), squarePaymentAttemptRepository: attempts);

            var recovered = await service.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(1, terminal.RecoveryCalls);
            Assert.Equal(0, terminal.AuthorizeCalls);
            Assert.Equal(1, api.AppendCalls);
            Assert.Equal("SQ:payment-001", api.LastAppendRequest!.Reference);
            Assert.Equal("payment-001", Assert.Single(api.LastAppendRequest.CardTransactions!).TxnRef);
            Assert.Equal(request.PaymentGuid, api.LastAppendRequest.PaymentGuid);
            Assert.Equal(request.IdempotencyKey, api.LastAppendRequest.IdempotencyKey);
            Assert.Equal("Square", claimState.LastBeginProvider);
            Assert.Equal(attemptGuid.ToString("D"), claimState.LastBeginProviderAttemptId);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Supervisor_confirmed_linkly_non_refund_persists_new_txn_ref_before_retry()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var terminal = new SequencedLinklyRefundTerminal(repository);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var settingsProvider = new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            });
            InstallmentOperationService CreateRestartedService() => new(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: settingsProvider);
            var service = CreateRestartedService();

            var initial = await service.ExecuteCancelAsync(order, Session, "客户取消");
            Assert.True(initial.RequiresReview);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            var step = Assert.Single((await repository.GetRefundStepsAsync(operation.OperationGuid)).Where(item => item.Method == PaymentMethodKind.Card));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, step.State);

            Assert.True(await repository.ResolveRefundStepAsync(
                step.RefundStepGuid,
                new InstallmentRefundSupervisorResolution(
                    InstallmentRefundSupervisorDecision.ConfirmNotRefunded,
                    "manager-1",
                    "银行明确确认未退款",
                    Evidence: "bank-case-001"),
                DateTimeOffset.UtcNow));

            var firstRetry = await CreateRestartedService().ResumeCancelAfterSupervisorAsync(operation.OperationGuid, order.InstallmentNumber, Session);
            Assert.True(firstRetry.RequiresReview);

            var secondUnknownStep = Assert.Single((await repository.GetRefundStepsAsync(operation.OperationGuid)).Where(item => item.Method == PaymentMethodKind.Card));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, secondUnknownStep.State);
            Assert.True(await repository.ResolveRefundStepAsync(
                secondUnknownStep.RefundStepGuid,
                new InstallmentRefundSupervisorResolution(
                    InstallmentRefundSupervisorDecision.ConfirmNotRefunded,
                    "manager-1",
                    "银行再次确认未退款",
                    Evidence: "bank-case-002"),
                DateTimeOffset.UtcNow));

            var resumed = await CreateRestartedService().ResumeCancelAfterSupervisorAsync(operation.OperationGuid, order.InstallmentNumber, Session);

            Assert.True(resumed.Succeeded, resumed.Message);
            Assert.Equal(3, terminal.RefundCalls);
            Assert.Equal(3, terminal.SubmissionReferences.Count);
            Assert.All(terminal.SubmissionReferences, reference => Assert.NotEqual("CARD-1", reference));
            Assert.All(terminal.SubmissionReferences, reference => AssertLocalTxnRef('R', reference));
            Assert.Equal(3, terminal.PersistedReferencesAtSubmission.Count);
            Assert.Equal(terminal.SubmissionReferences, terminal.PersistedReferencesAtSubmission);
            Assert.Equal(terminal.SubmissionReferences.Count, terminal.PersistedIdempotencyKeysAtSubmission.Count);
            for (var index = 0; index < terminal.SubmissionReferences.Count; index++)
            {
                Assert.Equal(
                    LinklyLocalTxnRef.Create('R', terminal.PersistedIdempotencyKeysAtSubmission[index]),
                    terminal.SubmissionReferences[index]);
            }
            Assert.NotEqual(terminal.SubmissionReferences[0], terminal.SubmissionReferences[1]);
            Assert.NotEqual(terminal.SubmissionReferences[1], terminal.SubmissionReferences[2]);
            Assert.Equal(2, terminal.RetryReferences.Count);
            Assert.All(terminal.RetryReferences, reference => Assert.NotEqual("CARD-1", reference));
            Assert.NotEqual(terminal.RetryReferences[0], terminal.RetryReferences[1]);
            Assert.Equal(terminal.RetryReferences, terminal.PersistedReferencesAtRetry);
            Assert.Equal(1, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
            Assert.NotNull(api.LastCancelClaimCommitRequest);
            Assert.All(api.LastCancelClaimCommitRequest!.Refunds, refund =>
            {
                var original = Assert.Single(order.Payments.Where(payment => payment.PaymentGuid == refund.OriginalPaymentGuid));
                Assert.Equal(original.Method, refund.Method);
                Assert.Equal(original.Amount, refund.Amount);
                Assert.Equal($"{operation.OperationGuid:D}:refund:{original.PaymentGuid:D}", refund.IdempotencyKey);
            });
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Refund_step_CAS_loser_does_not_call_terminal_or_overwrite_winner_reference()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var originalOrder = CreateLocalOrder();
            var cardPayment = originalOrder.Payments.Single(payment => payment.Method == PaymentMethodKind.Card);
            var order = originalOrder with
            {
                Payments = [cardPayment],
                PaidAmount = cardPayment.Amount
            };
            var terminal = new SequencedLinklyRefundTerminal(repository);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var settingsProvider = new RefundStepCasWinnerSettingsProvider(repository, Session.StoreCode);
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: settingsProvider);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Empty(terminal.SubmissionReferences);
            Assert.Equal(1, settingsProvider.GetSettingsCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(LocalInstallmentOperationState.TerminalSubmitting, operation.State);
            var step = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.TerminalSubmitting, step.State);
            Assert.Equal(RefundStepCasWinnerSettingsProvider.WinnerReference, step.RefundReference);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Linkly_session_binding_survives_the_cancelled_ui_token_after_terminal_accepts()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var terminal = new CancelledTokenSessionBindingTerminal(context);
            var request = CreateRepaymentRequest();
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var api = new RecordingInstallmentApi();
            var claimState = new RepaymentClaimTestState();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.RequiresReview);
            Assert.True(terminal.BindCalled);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            var attempt = await attempts.GetAttemptAsync(Guid.Parse(operation.TerminalAttemptGuid!));
            Assert.NotNull(attempt);
            Assert.Equal("LINKLY-SESSION-001", attempt.SessionId);
            Assert.Equal("LINKLY-TXN-001", attempt.TxnRef);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Repayment_approval_without_authorized_amount_remains_locked_and_does_not_call_api()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var terminal = new MissingAuthorizedAmountTerminal();
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(1, terminal.AuthorizeCalls);
            Assert.Equal(0, api.AppendCalls);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(request.PaymentGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Refund_approval_without_authorized_amount_remains_locked_and_does_not_call_cancel_api()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var terminal = new MissingAuthorizedAmountTerminal();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(1, terminal.RefundCalls);
            Assert.Equal(0, api.CancelCalls);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, operation.State);
            var cardStep = Assert.Single((await repository.GetRefundStepsAsync(operation.OperationGuid)).Where(step => step.Method == PaymentMethodKind.Card));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, cardStep.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cancel_claim_commit_failure_does_not_repeat_completed_refunds_after_restart()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var firstTerminal = new CountingTerminal(approve: true);
            // 重启只更换本地服务，远端 claim 状态必须持续存在。
            var remoteClaims = new Dictionary<Guid, InstallmentCancelClaimDto>();
            var firstApi = new RecordingInstallmentApi(remoteClaims)
            {
                CancelResponse = CreateCancelResponse(order),
                CancelClaimCommitException = new OperationCanceledException()
            };
            var firstService = CreateService(repository, firstApi, firstTerminal);

            var initial = await firstService.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(initial.Succeeded);
            Assert.True(initial.RequiresReview);
            Assert.Equal(1, firstTerminal.RefundCalls);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, operation.State);
            Assert.All(await repository.GetRefundStepsAsync(operation.OperationGuid), step => Assert.Equal(LocalInstallmentRefundStepState.Approved, step.State));

            var restartedTerminal = new CountingTerminal(approve: true);
            var restartedApi = new RecordingInstallmentApi(remoteClaims) { CancelResponse = CreateCancelResponse(order) };
            var restartedService = CreateService(repository, restartedApi, restartedTerminal);

            var recovered = await restartedService.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(0, restartedTerminal.RefundCalls);
            Assert.Equal(1, restartedApi.CommitCancelClaimCalls);
            Assert.Equal(0, restartedApi.CancelCalls);
            Assert.All(await repository.GetRefundStepsAsync(operation.OperationGuid), step => Assert.Equal(LocalInstallmentRefundStepState.Completed, step.State));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Explicit_refund_decline_terminates_claim_and_next_cancel_uses_new_operation()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateCardOnlyPaidOrder();
            var terminal = new DeclineThenApproveRefundTerminal();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(first.Succeeded);
            Assert.False(first.RequiresReview);
            Assert.Empty(await repository.GetRecoverableAsync(Session.StoreCode));
            var firstOperationGuid = Assert.Single(api.CreatedCancelOperationGuids);
            var operation = Assert.IsType<LocalInstallmentOperation>(await repository.GetAsync(firstOperationGuid));
            Assert.Equal(LocalInstallmentOperationState.Failed, operation.State);
            Assert.Equal(
                LocalInstallmentRefundStepState.Prepared,
                Assert.Single((await repository.GetRefundStepsAsync(operation.OperationGuid)).Where(step => step.Method == PaymentMethodKind.Card)).State);
            Assert.Equal(InstallmentCancelClaimResolveOutcome.Declined, Assert.Single(api.CancelResolveOutcomes));

            var second = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.True(second.Succeeded, second.Message);
            Assert.Equal(2, terminal.RefundCalls);
            Assert.Equal(2, api.CreatedCancelOperationGuids.Count);
            Assert.NotEqual(firstOperationGuid, api.CreatedCancelOperationGuids[1]);
            Assert.Equal(1, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Square_pending_refund_persists_identity_and_restart_get_completes_without_second_post()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSquareCancelOrder();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var context = new SquarePaymentAttemptContextAccessor();
            var lookup = new MutableSquareRefundStatusClient(
                new SquareRefundStatusResult("refund-pending", "PENDING", "payment-001", 4000, "AUD"));
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Square,
                Environment = CardTerminalEnvironment.Sandbox,
                SquareDeviceId = "device:TEST-DEVICE",
                SquareLocationId = "TEST-LOCATION"
            };
            var terminal = new PendingSquareRefundTerminal(context, settings.Environment);
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                squarePaymentAttemptContextAccessor: context,
                squareTerminalPaymentClient: lookup);

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(first.Succeeded);
            Assert.True(first.RequiresReview);
            Assert.Equal(1, terminal.RefundCalls);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            var pendingStep = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, pendingStep.State);
            Assert.Equal("SQRF:refund-pending", pendingStep.RefundReference);
            Assert.Equal(CardTerminalEnvironment.Sandbox.ToString(), pendingStep.ProviderEnvironment);
            Assert.Contains("PENDING", pendingStep.CardTransactionsJson);

            var restartedContext = new SquarePaymentAttemptContextAccessor();
            var restartedTerminal = new PendingSquareRefundTerminal(restartedContext, CardTerminalEnvironment.Production);
            var restartedSettings = settings with { Environment = CardTerminalEnvironment.Production };
            var restartedService = new InstallmentOperationService(
                new LocalInstallmentOperationRepository(new LocalSqliteStore(path)),
                api,
                restartedTerminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(restartedSettings),
                squarePaymentAttemptContextAccessor: restartedContext,
                squareTerminalPaymentClient: lookup);

            var pendingRecovery = Assert.Single(await restartedService.RecoverAsync(Session));

            Assert.False(pendingRecovery.ReplayedApi);
            Assert.Equal(1, lookup.GetRefundCalls);
            Assert.Equal(1, terminal.RefundCalls);
            Assert.Equal(0, restartedTerminal.RefundCalls);
            Assert.Equal(CardTerminalEnvironment.Sandbox, Assert.Single(lookup.RequestedEnvironments));
            Assert.Equal(
                LocalInstallmentRefundStepState.ResultUnknown,
                Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid)).State);

            lookup.Result = new SquareRefundStatusResult(
                "refund-pending",
                "COMPLETED",
                "payment-001",
                4000,
                "AUD",
                DateTimeOffset.UtcNow);
            var completedRecovery = Assert.Single(await restartedService.RecoverAsync(Session));

            Assert.True(completedRecovery.ReplayedApi);
            Assert.Equal(2, lookup.GetRefundCalls);
            Assert.Equal(1, terminal.RefundCalls);
            Assert.Equal(0, restartedTerminal.RefundCalls);
            Assert.Equal(1, api.CommitCancelClaimCalls);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(operation.OperationGuid))!.State);
            Assert.Equal(
                LocalInstallmentRefundStepState.Completed,
                Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid)).State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Square_completed_refund_with_mismatched_id_keeps_original_identity_for_next_recovery()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSquareCancelOrder();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var context = new SquarePaymentAttemptContextAccessor();
            var terminal = new PendingSquareRefundTerminal(context, CardTerminalEnvironment.Production);
            var lookup = new MutableSquareRefundStatusClient(
                new SquareRefundStatusResult("refund-other", "COMPLETED", "payment-001", 4000, "AUD"));
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Square,
                SquareDeviceId = "device:TEST-DEVICE",
                SquareLocationId = "TEST-LOCATION"
            };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                squarePaymentAttemptContextAccessor: context,
                squareTerminalPaymentClient: lookup);

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            var firstRecovery = Assert.Single(await service.RecoverAsync(Session));
            var secondRecovery = Assert.Single(await service.RecoverAsync(Session));

            Assert.False(first.Succeeded);
            Assert.False(firstRecovery.ReplayedApi);
            Assert.False(secondRecovery.ReplayedApi);
            Assert.Equal(2, lookup.GetRefundCalls);
            Assert.All(lookup.RequestedRefundIds, refundId => Assert.Equal("refund-pending", refundId));
            Assert.Equal(1, terminal.RefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(operation.OperationGuid))!.State);
            var step = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, step.State);
            Assert.Equal("SQRF:refund-pending", step.RefundReference);
            Assert.Contains("不匹配", step.FailureMessage);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Square_refund_callback_persists_identity_when_recovery_moves_step_to_unknown()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSquareCancelOrder();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var context = new SquarePaymentAttemptContextAccessor();
            var terminal = new GatedSquareRefundTerminal(context, CardTerminalEnvironment.Sandbox);
            var lookup = new MutableSquareRefundStatusClient(
                new SquareRefundStatusResult("refund-pending", "PENDING", "payment-001", 4000, "AUD"));
            var settings = CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Square,
                Environment = CardTerminalEnvironment.Sandbox
            };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                squarePaymentAttemptContextAccessor: context,
                squareTerminalPaymentClient: lookup);

            var cancelTask = service.ExecuteCancelAsync(order, Session, "客户取消");
            await terminal.RefundStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var recovery = await service.RecoverAsync(Session);
            terminal.ReleaseCallback.TrySetResult();
            var cancel = await cancelTask;

            Assert.False(cancel.Succeeded);
            Assert.True(cancel.RequiresReview);
            Assert.False(Assert.Single(recovery).ReplayedApi);
            Assert.Equal(1, terminal.RefundCalls);
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            var step = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, step.State);
            Assert.Equal("SQRF:refund-pending", step.RefundReference);
            Assert.Equal(CardTerminalEnvironment.Sandbox.ToString(), step.ProviderEnvironment);
            Assert.Contains("PENDING", step.CardTransactionsJson);
            Assert.Equal(0, api.CommitCancelClaimCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Square_settings_failure_keeps_refund_locked_without_aborting_recovery()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSquareCancelOrder();
            var operation = CreateCancelOperation(order, LocalInstallmentOperationState.ResultUnknown);
            var now = DateTimeOffset.UtcNow;
            var step = new LocalInstallmentRefundStep(
                Guid.NewGuid(), operation.OperationGuid, order.Payments[0].PaymentGuid, PaymentMethodKind.Card, 40m,
                "SQ:payment-001", "refund-idempotency", LocalInstallmentRefundStepState.ResultUnknown,
                "SQRF:refund-pending", "[]", null, null, null, null, null, null, now, now,
                CardTerminalEnvironment.Sandbox.ToString());
            await repository.CreateCancelOrGetAsync(operation, [step]);
            var service = new InstallmentOperationService(
                repository,
                new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) },
                new CountingTerminal(approve: true),
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new ThrowingCardTerminalSettingsProvider(),
                squareTerminalPaymentClient: new MutableSquareRefundStatusClient(
                    new SquareRefundStatusResult("refund-pending", "COMPLETED", "payment-001", 4000, "AUD")));

            var recovered = await service.RecoverAsync(Session);

            Assert.False(Assert.Single(recovered).ReplayedApi);
            var saved = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, saved.State);
            Assert.Contains("环境配置读取失败", saved.FailureMessage);
            Assert.Equal("SQRF:refund-pending", saved.RefundReference);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Square_rejected_refund_clears_evidence_and_rotates_idempotency_key()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSquareCancelOrder();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var context = new SquarePaymentAttemptContextAccessor();
            var terminal = new RejectedSquareRefundTerminal(context, CardTerminalEnvironment.Production);
            var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Square };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                squarePaymentAttemptContextAccessor: context);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            var operationGuid = Assert.Single(api.CreatedCancelOperationGuids);
            var saved = Assert.Single(await repository.GetRefundStepsAsync(operationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.Prepared, saved.State);
            Assert.Null(saved.RefundReference);
            Assert.Null(saved.ProviderEnvironment);
            Assert.Null(saved.CardTransactionsJson);
            Assert.Contains("REJECTED", saved.FailureMessage);
            Assert.NotEqual(Assert.Single(terminal.IdempotencyKeys), saved.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Square_recovery_rejected_status_clears_identity_and_does_not_query_old_refund_again()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSquareCancelOrder();
            var api = new RecordingInstallmentApi
            {
                CancelResponse = CreateCancelResponse(order),
                DeclinedCancelResolveFailuresRemaining = 1
            };
            var context = new SquarePaymentAttemptContextAccessor();
            var terminal = new PendingSquareRefundTerminal(context, CardTerminalEnvironment.Production);
            var lookup = new MutableSquareRefundStatusClient(
                new SquareRefundStatusResult("refund-pending", "REJECTED", "payment-001", 4000, "AUD", DateTimeOffset.UtcNow));
            var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Square };
            var service = new InstallmentOperationService(
                repository,
                api,
                terminal,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                squarePaymentAttemptContextAccessor: context,
                squareTerminalPaymentClient: lookup);

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");
            var operation = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            var pending = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            var originalIdempotencyKey = pending.IdempotencyKey;

            var rejectedRecovery = Assert.Single(await service.RecoverAsync(Session));
            var finalizedRecovery = Assert.Single(await service.RecoverAsync(Session));
            var thirdRecovery = await service.RecoverAsync(Session);

            Assert.False(first.Succeeded);
            Assert.False(rejectedRecovery.ReplayedApi);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, rejectedRecovery.State);
            Assert.Equal(LocalInstallmentOperationState.Failed, finalizedRecovery.State);
            Assert.Empty(thirdRecovery);
            Assert.Equal(1, lookup.GetRefundCalls);
            var saved = Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid));
            Assert.Equal(LocalInstallmentRefundStepState.Prepared, saved.State);
            Assert.Null(saved.RefundReference);
            Assert.Null(saved.ProviderEnvironment);
            Assert.Null(saved.CardTransactionsJson);
            Assert.Contains("REJECTED", saved.FailureMessage);
            Assert.NotEqual(originalIdempotencyKey, saved.IdempotencyKey);
            Assert.Equal(1, terminal.RefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(InstallmentCancelClaimResolveOutcome.Declined, api.CancelResolveOutcomes.Last());
            Assert.Equal(LocalInstallmentOperationState.Failed, (await repository.GetAsync(operation.OperationGuid))!.State);

            var retry = await service.ExecuteCancelAsync(order, Session, "重新取消");

            Assert.False(retry.Succeeded);
            Assert.Equal(2, terminal.RefundCalls);
            Assert.Equal(2, api.CreatedCancelOperationGuids.Count);
            Assert.NotEqual(api.CreatedCancelOperationGuids[0], api.CreatedCancelOperationGuids[1]);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Restarted_cancel_marks_submitting_refund_step_unknown_and_keeps_it_locked()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var operation = CreateCancelOperation(order, LocalInstallmentOperationState.TerminalSubmitting);
            var now = DateTimeOffset.UtcNow;
            var step = new LocalInstallmentRefundStep(
                Guid.NewGuid(), operation.OperationGuid, order.Payments[1].PaymentGuid, PaymentMethodKind.Card, 40m,
                "CARD-1", "refund-idempotency", LocalInstallmentRefundStepState.TerminalSubmitting,
                null, JsonSerializer.Serialize<IReadOnlyList<CardTransactionDto>>([CreateCardTransaction(40m)]), null,
                null, null, null, null, null, now, now);
            await repository.CreateCancelOrGetAsync(operation, [step]);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var service = new InstallmentOperationService(repository, api, new CountingTerminal(approve: true), new NoopVoucherTenderClient());

            var recovered = await service.RecoverAsync(Session);

            Assert.False(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(0, api.CancelCalls);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(operation.OperationGuid))!.State);
            Assert.Equal(LocalInstallmentRefundStepState.ResultUnknown, Assert.Single(await repository.GetRefundStepsAsync(operation.OperationGuid)).State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Restarted_cancel_with_all_refunds_approved_and_missing_remote_claim_stays_locked()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var operation = CreateCancelOperation(order, LocalInstallmentOperationState.TerminalSubmitting);
            var now = DateTimeOffset.UtcNow;
            var cardStep = new LocalInstallmentRefundStep(
                Guid.NewGuid(), operation.OperationGuid, order.Payments[1].PaymentGuid, PaymentMethodKind.Card, 40m,
                "CARD-1", "refund-idempotency", LocalInstallmentRefundStepState.Approved,
                "REFUND-1", JsonSerializer.Serialize<IReadOnlyList<CardTransactionDto>>([CreateCardTransaction(40m)]), null,
                null, null, null, null, null, now, now);
            var cashStep = new LocalInstallmentRefundStep(
                Guid.NewGuid(), operation.OperationGuid, order.Payments[0].PaymentGuid, PaymentMethodKind.Cash, 30m,
                "CASH-1", "cash-refund-idempotency", LocalInstallmentRefundStepState.Approved,
                "CASH-1", null, null, null, null, null, null, null, now, now);
            await repository.CreateCancelOrGetAsync(operation, [cashStep, cardStep]);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var terminal = new CountingTerminal(approve: true);
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var recovered = await service.RecoverAsync(Session);

            var recovery = Assert.Single(recovered);
            Assert.False(recovery.ReplayedApi);
            Assert.Contains("人工对账", recovery.Message);
            Assert.Equal(0, api.CreateCancelClaimCalls);
            Assert.Equal(0, api.BeginCancelRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(operation.OperationGuid))!.State);
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
    public async Task Missing_cancel_claim_after_refund_boundary_never_recreates_or_calls_provider(
        LocalInstallmentOperationState state)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var operation = CreateCancelOperation(order, state);
            var now = DateTimeOffset.UtcNow;
            var approvedSteps = order.Payments.Select(payment => new LocalInstallmentRefundStep(
                Guid.NewGuid(),
                operation.OperationGuid,
                payment.PaymentGuid,
                payment.Method,
                payment.Amount,
                payment.Reference,
                $"refund:{payment.PaymentGuid:D}",
                LocalInstallmentRefundStepState.Approved,
                $"REFUND:{payment.PaymentGuid:D}",
                payment.CardTransactions is null ? null : JsonSerializer.Serialize(payment.CardTransactions),
                null, null, null, null, null, null, now, now)).ToList();
            await repository.CreateCancelOrGetAsync(operation, approvedSteps);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var terminal = new CountingTerminal(approve: true);
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var recovery = Assert.Single(await service.RecoverAsync(Session));

            Assert.False(recovery.ReplayedApi);
            Assert.Contains("人工对账", recovery.Message);
            Assert.Equal(0, api.CreateCancelClaimCalls);
            Assert.Equal(0, api.BeginCancelRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, api.CancelCalls);
            Assert.NotEqual(LocalInstallmentOperationState.Completed, (await repository.GetAsync(operation.OperationGuid))!.State);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cancel_refund_plan_fingerprint_matches_backend_golden_vector()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var installmentGuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var now = DateTimeOffset.UtcNow;
            var order = CreateLocalOrder() with
            {
                OrderGuid = installmentGuid,
                InstallmentGuid = installmentGuid,
                PaidAmount = 37.75m,
                Payments =
                [
                    new InstallmentPaymentDto(Guid.Parse("40000000-0000-4000-8000-000000000001"), PaymentMethodKind.Voucher, 7.25m, "VIP-GOLDEN", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                    new InstallmentPaymentDto(Guid.Parse("20000000-0000-4000-8000-000000000001"), PaymentMethodKind.Cash, 20m, null, InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                    new InstallmentPaymentDto(Guid.Parse("30000000-0000-4000-8000-000000000001"), PaymentMethodKind.Card, 10.50m, "CARD-GOLDEN", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01")
                ]
            };
            var api = new RecordingInstallmentApi
            {
                CancelResponse = CreateCancelResponse(order),
                CancelClaimCreateException = new CatalogApiException("busy", System.Net.HttpStatusCode.Conflict, "INSTALLMENT_MUTATION_BUSY")
            };
            var service = new InstallmentOperationService(repository, api, new CountingTerminal(approve: true), new NoopVoucherTenderClient());

            var result = await service.ExecuteCancelAsync(order, Session, "golden vector");

            Assert.False(result.Succeeded);
            Assert.Equal(
                "sha256:e71e70a0dde391c395f87e43cbeb12056488ad6fbbd76622ba77761cf2b816e4",
                api.LastCancelClaimCreateRequest!.RefundPlanFingerprint);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Voucher_mode_cancel_issues_vouchers_for_every_tender_without_terminal_and_keeps_original_fingerprint()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var installmentGuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var now = DateTimeOffset.UtcNow;
            var order = CreateLocalOrder() with
            {
                OrderGuid = installmentGuid,
                InstallmentGuid = installmentGuid,
                PaidAmount = 37.75m,
                Payments =
                [
                    new InstallmentPaymentDto(Guid.Parse("40000000-0000-4000-8000-000000000001"), PaymentMethodKind.Voucher, 7.25m, "VIP-GOLDEN", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                    new InstallmentPaymentDto(Guid.Parse("20000000-0000-4000-8000-000000000001"), PaymentMethodKind.Cash, 20m, null, InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                    new InstallmentPaymentDto(Guid.Parse("30000000-0000-4000-8000-000000000001"), PaymentMethodKind.Card, 10.50m, "ANZ:CARD-GOLDEN", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01")
                ]
            };
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient(claimBegun: () => api.BeginCancelRefundCalls > 0);
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var result = await service.ExecuteCancelAsync(order, Session, "golden vector", InstallmentCancelRefundMode.Voucher);

            Assert.True(result.Succeeded, result.Message);
            // 指纹描述原付款计划，必须与原路退的黄金向量完全一致，服务端才能按原付款核对。
            Assert.Equal(
                "sha256:e71e70a0dde391c395f87e43cbeb12056488ad6fbbd76622ba77761cf2b816e4",
                api.LastCancelClaimCreateRequest!.RefundPlanFingerprint);
            Assert.Equal(InstallmentCancelRefundMode.Voucher, api.LastCancelClaimCreateRequest.RefundMode);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(3, voucher.IssueRefundCalls);
            Assert.True(voucher.ClaimWasBegunBeforeRefund);
            var refunds = api.LastCancelClaimCommitRequest!.Refunds;
            Assert.Equal(3, refunds.Count);
            Assert.All(refunds, refund =>
            {
                Assert.Equal(PaymentMethodKind.Voucher, refund.Method);
                Assert.True(refund.CardTransactions is null or { Count: 0 });
            });
            Assert.Equal(10.50m, refunds.Single(refund => refund.OriginalPaymentGuid == order.Payments[2].PaymentGuid).Amount);

            var steps = await repository.GetRefundStepsAsync(api.CreatedCancelOperationGuids[0]);
            var cardStep = steps.Single(step => step.OriginalPaymentGuid == order.Payments[2].PaymentGuid);
            Assert.Equal(PaymentMethodKind.Voucher, cardStep.Method);
            Assert.Equal(PaymentMethodKind.Card, cardStep.OriginalMethod);
            Assert.Null(cardStep.OriginalReference);
            Assert.Null(cardStep.CardTransactionsJson);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cancel_claim_busy_stops_before_any_refund()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi
            {
                CancelResponse = CreateCancelResponse(order),
                CancelClaimCreateException = new CatalogApiException(
                    "busy",
                    System.Net.HttpStatusCode.Conflict,
                    "INSTALLMENT_MUTATION_BUSY")
            };
            var service = CreateService(repository, api, terminal);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, api.CancelCalls);
            Assert.Equal(1, api.CreateCancelClaimCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData("Square")]
    [InlineData("Linkly")]
    [InlineData("Mixed")]
    public async Task Unsupported_cancel_refund_method_at_claim_create_has_no_side_effects_and_next_attempt_uses_new_operation(
        string paymentScenario)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateUnsupportedCancelOrder(paymentScenario);
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient();
            var api = new RecordingInstallmentApi
            {
                CancelClaimCreateException = new CatalogApiException(
                    "unsupported refund method",
                    System.Net.HttpStatusCode.Conflict,
                    "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED")
            };
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");
            var second = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(first.Succeeded);
            Assert.False(first.RequiresReview);
            Assert.False(second.Succeeded);
            Assert.False(second.RequiresReview);
            Assert.Equal(2, api.CreatedCancelOperationGuids.Count);
            Assert.NotEqual(api.CreatedCancelOperationGuids[0], api.CreatedCancelOperationGuids[1]);
            foreach (var operationGuid in api.CreatedCancelOperationGuids)
            {
                Assert.Equal(LocalInstallmentOperationState.Failed, (await repository.GetAsync(operationGuid))!.State);
            }
            Assert.Empty(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(0, api.BeginCancelRefundCalls);
            Assert.Empty(api.CancelResolveOutcomes);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, voucher.IssueRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unsupported_cancel_claim_read_with_non_prepared_refund_step_stays_locked()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSupportedCancelOrder();
            var operation = CreateCancelOperation(order, LocalInstallmentOperationState.Prepared);
            var payment = order.Payments[0];
            var now = DateTimeOffset.UtcNow;
            var step = new LocalInstallmentRefundStep(
                Guid.NewGuid(),
                operation.OperationGuid,
                payment.PaymentGuid,
                payment.Method,
                payment.Amount,
                payment.Reference,
                $"{operation.OperationGuid:D}:refund:{payment.PaymentGuid:D}",
                LocalInstallmentRefundStepState.ResultUnknown,
                null,
                null,
                "previous result unknown",
                null,
                null,
                null,
                null,
                null,
                now,
                now);
            await repository.CreateCancelOrGetAsync(operation, [step]);
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient();
            var api = new RecordingInstallmentApi
            {
                CancelClaimGetException = new CatalogApiException(
                    "unsupported refund method",
                    System.Net.HttpStatusCode.Conflict,
                    "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED")
            };
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(operation.OperationGuid))!.State);
            Assert.Equal(0, api.CreateCancelClaimCalls);
            Assert.Equal(0, api.BeginCancelRefundCalls);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, voucher.IssueRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unsupported_cancel_refund_method_at_begin_releases_claim_and_next_attempt_uses_new_operation()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSupportedCancelOrder();
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient();
            var api = new RecordingInstallmentApi
            {
                CancelClaimBeginException = new CatalogApiException(
                    "unsupported refund method",
                    System.Net.HttpStatusCode.Conflict,
                    "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED")
            };
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");
            var second = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(first.Succeeded);
            Assert.False(first.RequiresReview);
            Assert.False(second.Succeeded);
            Assert.False(second.RequiresReview);
            Assert.Equal(2, api.CreatedCancelOperationGuids.Count);
            Assert.NotEqual(api.CreatedCancelOperationGuids[0], api.CreatedCancelOperationGuids[1]);
            Assert.Equal(
                [InstallmentCancelClaimResolveOutcome.Released, InstallmentCancelClaimResolveOutcome.Released],
                api.CancelResolveOutcomes);
            foreach (var operationGuid in api.CreatedCancelOperationGuids)
            {
                Assert.Equal(LocalInstallmentOperationState.Failed, (await repository.GetAsync(operationGuid))!.State);
            }
            Assert.Empty(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, voucher.IssueRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Unsupported_cancel_refund_method_at_begin_keeps_lock_when_claim_release_fails()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSupportedCancelOrder();
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient();
            var api = new RecordingInstallmentApi
            {
                CancelClaimBeginException = new CatalogApiException(
                    "unsupported refund method",
                    System.Net.HttpStatusCode.Conflict,
                    "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED"),
                CancelClaimResolveException = new HttpRequestException("release response lost")
            };
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            var operationGuid = Assert.Single(api.CreatedCancelOperationGuids);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(operationGuid))!.State);
            Assert.Equal(InstallmentCancelClaimResolveOutcome.Released, Assert.Single(api.CancelResolveOutcomes));
            Assert.Contains(order.InstallmentGuid, await service.GetLockedInstallmentGuidsAsync(Session));
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, voucher.IssueRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(InstallmentCancelClaimStatus.Released)]
    [InlineData(InstallmentCancelClaimStatus.Declined)]
    public async Task Expired_cancel_begin_only_terminates_after_remote_terminal_status_is_confirmed(
        InstallmentCancelClaimStatus remoteStatus)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSupportedCancelOrder();
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient();
            var api = new RecordingInstallmentApi
            {
                CancelClaimBeginException = new CatalogApiException(
                    "claim expired",
                    System.Net.HttpStatusCode.Conflict,
                    "INSTALLMENT_CANCEL_CLAIM_EXPIRED"),
                CancelClaimStatusAfterBeginException = remoteStatus
            };
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.False(result.RequiresReview);
            var operationGuid = Assert.Single(api.CreatedCancelOperationGuids);
            Assert.Equal(LocalInstallmentOperationState.Failed, (await repository.GetAsync(operationGuid))!.State);
            Assert.True(api.GetCancelClaimCalls >= 3);
            Assert.Empty(api.CancelResolveOutcomes);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, voucher.IssueRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_cancel_begin_failure_stays_locked_without_provider_side_effects(bool remoteUnknown)
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSupportedCancelOrder();
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient();
            var api = new RecordingInstallmentApi
            {
                CancelClaimBeginException = remoteUnknown
                    ? new CatalogApiException("claim expired", System.Net.HttpStatusCode.Conflict, "INSTALLMENT_CANCEL_CLAIM_EXPIRED")
                    : new HttpRequestException("begin response lost"),
                CancelClaimStatusAfterBeginException = remoteUnknown
                    ? InstallmentCancelClaimStatus.Unknown
                    : null
            };
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            var operationGuid = Assert.Single(api.CreatedCancelOperationGuids);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, (await repository.GetAsync(operationGuid))!.State);
            Assert.Contains(order.InstallmentGuid, await service.GetLockedInstallmentGuidsAsync(Session));
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, voucher.IssueRefundCalls);
            Assert.Equal(0, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cancel_begins_claim_before_refund_and_commits_without_legacy_cancel()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateSupportedCancelOrder();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var terminal = new CountingTerminal(approve: true);
            var voucher = new CountingVoucherTenderClient(claimBegun: () => api.BeginCancelRefundCalls > 0);
            var service = new InstallmentOperationService(repository, api, terminal, voucher);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.True(result.Succeeded, result.Message);
            Assert.True(voucher.ClaimWasBegunBeforeRefund);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(1, voucher.IssueRefundCalls);
            Assert.Equal(1, api.CommitCancelClaimCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cancel_commit_response_loss_reads_committed_claim_without_replaying_refund()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi
            {
                CancelResponse = CreateCancelResponse(order),
                LoseFirstCancelCommitResponse = true
            };
            var service = CreateService(repository, api, terminal);

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(1, terminal.RefundCalls);
            Assert.Equal(1, api.CommitCancelClaimCalls);
            Assert.True(api.GetCancelClaimCalls >= 2);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Fully_declined_cancel_terminates_claim_and_next_attempt_uses_new_operation()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateCardOnlyPaidOrder();
            var terminal = new CountingTerminal(approve: false);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var service = CreateService(repository, api, terminal);

            var first = await service.ExecuteCancelAsync(order, Session, "客户取消");
            var second = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(first.Succeeded);
            Assert.False(second.Succeeded);
            Assert.Equal(2, api.CreatedCancelOperationGuids.Count);
            Assert.NotEqual(api.CreatedCancelOperationGuids[0], api.CreatedCancelOperationGuids[1]);
            Assert.Equal(
                [InstallmentCancelClaimResolveOutcome.Declined, InstallmentCancelClaimResolveOutcome.Declined],
                api.CancelResolveOutcomes);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Partial_refund_then_unknown_resolves_central_claim_with_approved_snapshot()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var terminal = new UnknownRefundTerminal();
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var service = new InstallmentOperationService(repository, api, terminal, new NoopVoucherTenderClient());

            var result = await service.ExecuteCancelAsync(order, Session, "客户取消");

            Assert.False(result.Succeeded);
            Assert.True(result.RequiresReview);
            var resolve = Assert.Single(api.CancelResolveRequests);
            Assert.Equal(InstallmentCancelClaimResolveOutcome.Unknown, resolve.Outcome);
            var approved = Assert.Single(resolve.ApprovedRefunds!);
            Assert.Equal(PaymentMethodKind.Cash, approved.Method);
            Assert.Equal(30m, approved.Amount);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cancel_from_non_origin_device_stops_before_claim_and_refund()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var order = CreateLocalOrder();
            var terminal = new CountingTerminal(approve: true);
            var api = new RecordingInstallmentApi { CancelResponse = CreateCancelResponse(order) };
            var service = CreateService(repository, api, terminal);
            var otherDevice = Session with { DeviceCode = "POS-02" };

            var result = await service.ExecuteCancelAsync(order, otherDevice, "客户取消");

            Assert.False(result.Succeeded);
            Assert.Equal(0, api.CreateCancelClaimCalls);
            Assert.Equal(0, terminal.RefundCalls);
            Assert.Equal(0, api.CancelCalls);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Pickup_timeout_is_locked_and_restart_replays_only_the_same_idempotent_request()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(path);
            var installmentGuid = Guid.NewGuid();
            var request = new InstallmentConfirmPickupRequest(
                installmentGuid,
                Session.StoreCode,
                Session.DeviceCode,
                Session.CashierId,
                Session.CashierName,
                DateTimeOffset.UtcNow,
                OperationGuid: installmentGuid,
                IdempotencyKey: $"{installmentGuid:D}:pickup");
            var firstApi = new RecordingInstallmentApi
            {
                PickupException = new TaskCanceledException("pickup timeout")
            };
            var firstService = CreateService(repository, firstApi, new CountingTerminal(approve: true));

            var first = await firstService.ExecutePickupAsync(Session, request);
            var repeatedClick = await firstService.ExecutePickupAsync(Session, request);

            Assert.False(first.Succeeded);
            Assert.True(first.RequiresReview);
            Assert.False(repeatedClick.Succeeded);
            Assert.True(repeatedClick.RequiresReview);
            Assert.Equal(1, firstApi.PickupCalls);
            var unknown = Assert.Single(await repository.GetRecoverableAsync(Session.StoreCode));
            Assert.Equal(LocalInstallmentOperationKind.Pickup, unknown.Kind);
            Assert.Equal(LocalInstallmentOperationState.ResultUnknown, unknown.State);
            Assert.Contains(installmentGuid, await firstService.GetLockedInstallmentGuidsAsync(Session));

            var pickedUpAt = DateTimeOffset.UtcNow;
            var details = CreateDetails(installmentGuid, InstallmentStatus.PickedUp) with
            {
                PaidAmount = 120m,
                BalanceAmount = 0m,
                PickupInfo = new InstallmentPickupInfoDto(pickedUpAt, Session.CashierName, null)
            };
            var restartedApi = new RecordingInstallmentApi
            {
                PickupResponse = new InstallmentConfirmPickupResponse(
                    installmentGuid,
                    InstallmentStatus.PickedUp,
                    pickedUpAt,
                    details)
            };
            var restartedService = CreateService(repository, restartedApi, new CountingTerminal(approve: true));

            var recovery = Assert.Single(await restartedService.RecoverAsync(Session));

            Assert.True(recovery.ReplayedApi);
            Assert.Equal(1, restartedApi.PickupCalls);
            Assert.Equal(request.OperationGuid, restartedApi.LastPickupRequest!.OperationGuid);
            Assert.Equal(request.IdempotencyKey, restartedApi.LastPickupRequest.IdempotencyKey);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(installmentGuid))!.State);
            Assert.Empty(await restartedService.GetLockedInstallmentGuidsAsync(Session));
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_installment_purchase_persists_derived_txn_ref_before_terminal_call()
    {
        // 后端模式过去不预派生 TxnRef，POST 已受理而响应丢失时本地既无 SessionId 也无 TxnRef，恢复无法认领会话。
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var terminal = new PersistedLinklyPurchaseTerminal(attempts, context);
            var service = CreateBackendInstallmentService(
                repository, attempts, context, terminal, new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) }, request,
                new AckRecordingBackendClient());

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.NotNull(terminal.AttemptGuid);
            var persisted = await attempts.GetAttemptAsync(terminal.AttemptGuid.Value);
            Assert.NotNull(persisted);
            AssertLocalTxnRef('P', persisted.TxnRef);
            Assert.Equal(LinklyLocalTxnRef.Create('P', persisted.AttemptGuid.ToString("D")), persisted.TxnRef);
            Assert.Equal(persisted.TxnRef, terminal.SubmittedTxnRef);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_repayment_acknowledges_the_linkly_session_after_the_local_snapshot_completes()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var backend = new AckRecordingBackendClient();
            var terminal = new BackendSessionBindingTerminal(context, approve: true);
            var service = CreateBackendInstallmentService(
                repository, attempts, context, terminal, new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) }, request, backend);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal([BackendSessionBindingTerminal.SessionId], backend.AcknowledgedSessionIds);
            Assert.Equal(CardTerminalEnvironment.Sandbox, backend.AcknowledgedSettings!.Environment);
            var attempt = await attempts.GetAttemptAsync(terminal.AttemptGuid!.Value);
            Assert.NotNull(attempt!.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_installment_create_acknowledges_the_linkly_session_after_the_local_snapshot_completes()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateInstallmentRequest(Guid.NewGuid());
            var details = CreateDetails(request.InstallmentGuid, InstallmentStatus.Active);
            var api = new RecordingInstallmentApi
            {
                CreateResponse = new InstallmentCreateResponse(request.InstallmentGuid, "IO-001", InstallmentStatus.Active, 30m, 90m, details)
            };
            var backend = new AckRecordingBackendClient();
            var service = new InstallmentOperationService(
                repository,
                api,
                new BackendSessionBindingTerminal(context, approve: true),
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(BackendSettings()),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context,
                linklyBackendTerminalClient: backend);

            var result = await service.ExecuteCreateAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal([BackendSessionBindingTerminal.SessionId], backend.AcknowledgedSessionIds);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_repayment_acknowledges_the_linkly_session_after_an_explicit_decline()
    {
        // 明确拒绝同样留下未确认的终态会话；不 ack 会让下一笔刷卡被 409 拦下并接管出待复核记录。
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var backend = new AckRecordingBackendClient();
            var service = CreateBackendInstallmentService(
                repository, attempts, context, new BackendSessionBindingTerminal(context, approve: false),
                new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) }, request, backend);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.Equal([BackendSessionBindingTerminal.SessionId], backend.AcknowledgedSessionIds);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_repayment_does_not_acknowledge_while_the_central_commit_is_unknown()
    {
        // ack 只能发生在本地终态持久化之后：中央提交结果未知时会话必须保持未确认，恢复仍可按会话核验。
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var backend = new AckRecordingBackendClient();
            var service = CreateBackendInstallmentService(
                repository, attempts, context, new BackendSessionBindingTerminal(context, approve: true),
                new RecordingInstallmentApi { AppendException = new HttpRequestException("gateway") }, request, backend);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.False(result.Succeeded);
            Assert.Empty(backend.AcknowledgedSessionIds);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_repayment_stays_completed_when_the_linkly_acknowledge_fails()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var backend = new AckRecordingBackendClient { AcknowledgeException = new HttpRequestException("ack failed") };
            var terminal = new BackendSessionBindingTerminal(context, approve: true);
            var service = CreateBackendInstallmentService(
                repository, attempts, context, terminal, new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) }, request, backend);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(LocalInstallmentOperationState.Completed, (await repository.GetAsync(request.PaymentGuid))!.State);
            // 本地未确认标记保持为空，后续接管/重试仍能补发。
            Assert.Null((await attempts.GetAttemptAsync(terminal.AttemptGuid!.Value))!.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Direct_cloud_repayment_never_calls_the_backend_acknowledge()
    {
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var context = new LinklyPaymentAttemptContextAccessor();
            var request = CreateRepaymentRequest();
            var backend = new AckRecordingBackendClient();
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(
                    new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) }, request, new RepaymentClaimTestState()),
                new BackendSessionBindingTerminal(context, approve: true),
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(
                    BackendSettings() with { LinklyConnectionMode = LinklyConnectionMode.CloudDirectSync }),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: context,
                linklyBackendTerminalClient: backend);

            var result = await service.ExecuteRepaymentAsync(Session, request, authorizeCard: true);

            Assert.True(result.Succeeded, result.Message);
            Assert.Empty(backend.AcknowledgedSessionIds);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    [Fact]
    public async Task Cloud_backend_recovery_approves_from_the_verified_status_card_transaction_then_replays_only_api()
    {
        // 端到端：卡已批准但 WPF 在提交中央前崩溃。恢复必须用状态接口已核验的 CardTransaction 认定批准，
        // 只补提交 API（不重扣），并在本地完成后确认会话。
        var path = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(path);
            await new LocalSchemaService(store).InitializeAsync();
            var repository = new LocalInstallmentOperationRepository(store);
            var attempts = new LocalCardPaymentAttemptRepository(store);
            var request = CreateRepaymentRequest();
            var attemptGuid = Guid.Parse("f588a502-9129-e8ad-8627-3d9e1b2b892a");
            var txnRef = LinklyLocalTxnRef.Create('P', attemptGuid.ToString("D"));
            var now = DateTimeOffset.UtcNow;
            await attempts.CreateAsync(new LocalCardPaymentAttempt(
                attemptGuid, BackendSessionBindingTerminal.SessionId, txnRef, "Linkly", "Sandbox", "CloudBackendAsync", "P", request.Amount,
                LocalCardPaymentAttemptStatus.Recovering, "{}", request.StoreCode, request.DeviceCode, request.CashierId,
                null, null, null, now, now, null, null, "Repayment", request.PaymentGuid));
            await repository.CreateOrGetAsync(CreateApprovedRepaymentOperation(request) with
            {
                State = LocalInstallmentOperationState.ResultUnknown,
                TerminalAttemptGuid = attemptGuid.ToString("D"),
                RequestJson = JsonSerializer.Serialize(request)
            });
            var backend = new AckRecordingBackendClient
            {
                Status = CreateApprovedBackendStatus(txnRef, (long)(request.Amount * 100m))
            };
            var settings = BackendSettings();
            var configured = new ConfiguredCardTerminalClient(
                new StaticCardTerminalSettingsProvider(settings),
                new HttpClient(),
                linklyBackendTerminalClient: backend);
            var api = new RecordingInstallmentApi { AppendResponse = CreateAppendResponse(request) };
            var claimState = new RepaymentClaimTestState();
            claimState.Seed(request, InstallmentRepaymentClaimStatus.Unknown, "Linkly", attemptGuid.ToString("D"));
            var service = new InstallmentOperationService(
                repository,
                new ClaimAwareInstallmentApiTestAdapter(api, request, claimState),
                configured,
                new NoopVoucherTenderClient(),
                cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings),
                cardPaymentAttemptRepository: attempts,
                linklyPaymentAttemptContextAccessor: new LinklyPaymentAttemptContextAccessor(),
                linklyBackendTerminalClient: backend);

            var recovered = await service.RecoverAsync(Session);

            Assert.True(Assert.Single(recovered).ReplayedApi);
            Assert.Equal(1, api.AppendCalls);
            var card = Assert.Single(api.LastAppendRequest!.CardTransactions!);
            Assert.Equal(txnRef, card.TxnRef);
            Assert.Equal(request.Amount, card.Amount);
            Assert.Equal([BackendSessionBindingTerminal.SessionId], backend.AcknowledgedSessionIds);
        }
        finally
        {
            DeleteTempDatabase(path);
        }
    }

    private static readonly PosSessionState Session = new("HB POS", "S001", "Main", "POS-01", "C001", "Alice", true, 0);

    private static CardTerminalSettings BackendSettings() => CardTerminalSettings.FromEnvironment() with
    {
        Processor = CardProcessorKind.Linkly,
        Environment = CardTerminalEnvironment.Sandbox,
        LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
    };

    private static InstallmentOperationService CreateBackendInstallmentService(
        LocalInstallmentOperationRepository repository,
        LocalCardPaymentAttemptRepository attempts,
        LinklyPaymentAttemptContextAccessor context,
        ICardTerminalClient terminal,
        RecordingInstallmentApi api,
        InstallmentAppendPaymentRequest request,
        AckRecordingBackendClient backend) =>
        new(
            repository,
            new ClaimAwareInstallmentApiTestAdapter(api, request, new RepaymentClaimTestState()),
            terminal,
            new NoopVoucherTenderClient(),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(BackendSettings()),
            cardPaymentAttemptRepository: attempts,
            linklyPaymentAttemptContextAccessor: context,
            linklyBackendTerminalClient: backend);

    private static LinklyCloudBackendSessionResponse CreateApprovedBackendStatus(string txnRef, long amountCents) =>
        new(
            "Sandbox", "S001", "POS-01", BackendSessionBindingTerminal.SessionId, "Completed", txnRef, "00", "APPROVED", null, "APPROVED",
            false, false, false, false, false, null, null, null, "RECEIPT", 0, null, null, 200, [], TransactionSuccess: true)
        {
            CardTransaction = new LinklyCloudBackendCardTransactionDto(
                txnRef, null, "AUTH01", "VISA", "************1234", "MID01", "00", "APPROVED", "000123", DateTimeOffset.UtcNow, amountCents)
        };

    private sealed class AckRecordingBackendClient : ILinklyBackendTerminalClient
    {
        public Exception? AcknowledgeException { get; init; }

        public LinklyCloudBackendSessionResponse? Status { get; init; }

        public List<string> AcknowledgedSessionIds { get; } = [];

        public CardTerminalSettings? AcknowledgedSettings { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> PurchaseAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(CardTerminalSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(CardTerminalSettings settings, LinklyCloudBackendSessionResponse activeStatus, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
            Status is null ? throw new NotSupportedException() : Task.FromResult(Status);

        public Task AcknowledgeSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            AcknowledgedSettings = settings;
            if (AcknowledgeException is not null)
            {
                throw AcknowledgeException;
            }

            AcknowledgedSessionIds.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task AcknowledgeSupervisorResolvedSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>模拟后端客户端：先把后端会话绑定到 attempt（落 SessionId），再返回批准或明确拒绝。</summary>
    private sealed class BackendSessionBindingTerminal(
        ILinklyPaymentAttemptContextAccessor context,
        bool approve) : ICardTerminalClient
    {
        public const string SessionId = "7d9f6c1e-0000-4000-8000-000000000001";

        public Guid? AttemptGuid { get; private set; }

        public async Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            var current = context.Current ?? throw new InvalidOperationException("Linkly attempt context was not available.");
            AttemptGuid = current.AttemptGuid;
            await current.BindSessionAsync(SessionId, current.TxnRef, DateTimeOffset.UtcNow, cancellationToken);
            return approve
                ? new PaymentAuthorizationResult(
                    true, $"ANZBACKEND:{current.TxnRef}", "APPROVED", amount,
                    [new CardTransactionDto("ANZ", current.TxnRef, null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, amount, null)],
                    "ANZ", "Sandbox", LinklyConnectionMode.CloudBackendAsync.ToString(), "P", SessionId, current.TxnRef, "00", "APPROVED")
                : new PaymentAuthorizationResult(
                    false, null, "DECLINED", Processor: "ANZ", SessionId: SessionId, TxnRef: current.TxnRef, ResponseCode: "05", ResponseText: "DECLINED");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static void AssertLocalTxnRef(char transactionType, string? txnRef)
    {
        Assert.NotNull(txnRef);
        Assert.Equal(16, txnRef.Length);
        Assert.Equal(transactionType, txnRef[0]);
        Assert.Matches("^[PR][0-9ABCDEFGHJKMNPQRSTVWXYZ]{15}$", txnRef);
    }

    private static async Task<LocalInstallmentOperationRepository> CreateRepositoryAsync(string path)
    {
        var store = new LocalSqliteStore(path);
        await new LocalSchemaService(store).InitializeAsync();
        return new LocalInstallmentOperationRepository(store);
    }

    private static InstallmentOperationService CreateService(
        LocalInstallmentOperationRepository repository,
        IInstallmentApiClient api,
        CountingTerminal terminal)
    {
        // 固定 Linkly 路由，避免环境变量让卡支付夹具漂移到其他终端实现。
        var settings = CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.LocalIp
        };
        return new(repository, api, terminal, new NoopVoucherTenderClient(),
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(settings));
    }

    private static InstallmentAppendPaymentRequest CreateRepaymentRequest() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
        "S001", "POS-01", "C001", "Alice", 40m, PaymentMethodKind.Card, null, null, null, "repayment-idempotency");

    private static LocalCardPaymentAttempt CreatePersistedInstallmentLinklyAttempt(
        InstallmentAppendPaymentRequest request,
        LinklyConnectionMode connectionMode,
        string txnType,
        string? txnRef)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalCardPaymentAttempt(
            Guid.Parse("f588a502-9129-e8ad-8627-3d9e1b2b892a"),
            null,
            txnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Sandbox.ToString(),
            connectionMode.ToString(),
            txnType,
            request.Amount,
            LocalCardPaymentAttemptStatus.Pending,
            JsonSerializer.Serialize(request),
            request.StoreCode,
            request.DeviceCode,
            request.CashierId,
            null,
            null,
            null,
            now,
            now,
            null,
            null,
            "Repayment",
            request.PaymentGuid);
    }

    private static InstallmentCreateRequest CreateInstallmentRequest(Guid paymentGuid) => new(
        paymentGuid,
        "S001",
        "POS-01",
        "C001",
        "Alice",
        DateTimeOffset.UtcNow,
        120m,
        30m,
        [new InstallmentLineDto(Guid.NewGuid(), "SKU-001", null, "Rice Cooker", "690001", 1m, 120m, 0m, 120m)],
        new InstallmentPaymentCommandDto(
            paymentGuid,
            PaymentMethodKind.Card,
            30m,
            null,
            IdempotencyKey: $"{paymentGuid:D}:create"),
        "Alice",
        "0400111222");

    private static LocalInstallmentOperation CreateApprovedRepaymentOperation(InstallmentAppendPaymentRequest request)
    {
        var transaction = CreateCardTransaction(request.Amount);
        var snapshot = request with { Reference = transaction.TxnRef, CardTransactions = [transaction] };
        var now = DateTimeOffset.UtcNow;
        return new LocalInstallmentOperation(request.PaymentGuid, LocalInstallmentOperationKind.Repayment, request.InstallmentGuid, request.PaymentGuid, request.StoreCode, request.DeviceCode, request.CashierId, request.IdempotencyKey!, JsonSerializer.Serialize(snapshot), LocalInstallmentOperationState.TerminalApproved, "attempt", "Linkly", null, null, now, now);
    }

    private static LocalInstallmentOperation CreateCancelOperation(LocalInstallmentOrder order, LocalInstallmentOperationState state)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new InstallmentCancelRequest(
            order.InstallmentGuid,
            Session.StoreCode,
            Session.DeviceCode,
            Session.CashierId,
            Session.CashierName,
            now,
            [],
            "客户取消",
            $"{order.InstallmentGuid:D}:cancel");
        return new LocalInstallmentOperation(
            Guid.NewGuid(),
            LocalInstallmentOperationKind.Cancel,
            order.InstallmentGuid,
            null,
            Session.StoreCode,
            Session.DeviceCode,
            Session.CashierId,
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

    private static InstallmentAppendPaymentResponse CreateAppendResponse(InstallmentAppendPaymentRequest request)
    {
        var details = CreateDetails(request.InstallmentGuid, InstallmentStatus.Active) with
        {
            PaidAmount = 70m,
            BalanceAmount = 50m,
            Payments =
            [
                new InstallmentPaymentDto(Guid.Parse("12345678-1111-2222-3333-444444444444"), PaymentMethodKind.Cash, 30m, null, InstallmentPaymentStatus.Recorded, DateTimeOffset.UtcNow, "C001", "POS-01"),
                new InstallmentPaymentDto(request.PaymentGuid, request.Method, request.Amount, request.Reference, InstallmentPaymentStatus.Recorded, DateTimeOffset.UtcNow, "C001", "POS-01", request.CardTransactions, request.IdempotencyKey)
            ]
        };
        return new InstallmentAppendPaymentResponse(request.InstallmentGuid, request.PaymentGuid, details.PaidAmount, details.BalanceAmount, details.Status, details, false, "补款完成");
    }

    private static LocalInstallmentOrder CreateLocalOrder()
    {
        var details = CreateDetails(Guid.Parse("11111111-2222-3333-4444-555555555555"), InstallmentStatus.Active) with
        {
            Payments =
            [
                new InstallmentPaymentDto(Guid.Parse("12345678-1111-2222-3333-444444444444"), PaymentMethodKind.Cash, 30m, "CASH-1", InstallmentPaymentStatus.Recorded, DateTimeOffset.UtcNow, "C001", "POS-01"),
                new InstallmentPaymentDto(Guid.Parse("12345678-2222-3333-4444-555555555555"), PaymentMethodKind.Card, 40m, "CARD-1", InstallmentPaymentStatus.Recorded, DateTimeOffset.UtcNow, "C001", "POS-01", [CreateCardTransaction(40m)])
            ],
            PaidAmount = 70m,
            BalanceAmount = 50m
        };
        return new LocalInstallmentOrder(details.InstallmentGuid, details.InstallmentGuid, details.InstallmentNumber, details.StoreCode, details.DeviceCode, details.CashierId, details.CashierName, details.CustomerName, details.CustomerPhone, details.CreatedAt, DateTimeOffset.UtcNow, details.TotalAmount, details.MinimumDownPayment, details.DownPaymentAmount, details.PaidAmount, details.BalanceAmount, details.Status, details.Lines, details.Payments, details.PickupInfo, details.Note, details.CancellationInfo);
    }

    private static LocalInstallmentOrder CreateCardOnlyPaidOrder()
    {
        var order = CreateLocalOrder();
        var cardPayment = order.Payments.Single(payment => payment.Method == PaymentMethodKind.Card);

        // 拒绝退款只覆盖实际已付的卡款，避免夹具遗留现金付款导致退款计划失真。
        return order with
        {
            Payments = [cardPayment],
            PaidAmount = cardPayment.Amount,
            BalanceAmount = order.TotalAmount - cardPayment.Amount
        };
    }

    private static LocalInstallmentOrder CreateSquareCancelOrder()
    {
        var order = CreateLocalOrder();
        var now = DateTimeOffset.UtcNow;
        var payment = new InstallmentPaymentDto(
            Guid.Parse("12345678-2222-3333-4444-555555555555"),
            PaymentMethodKind.Card,
            40m,
            "SQ:payment-001",
            InstallmentPaymentStatus.Recorded,
            now,
            "C001",
            "POS-01",
            [new CardTransactionDto("Square", "payment-001", null, null, null, null, null, null, "COMPLETED", null, now, 40m, null)]);
        return order with
        {
            Payments = [payment],
            PaidAmount = payment.Amount,
            BalanceAmount = order.TotalAmount - payment.Amount
        };
    }

    private static LocalInstallmentOrder CreateSupportedCancelOrder()
    {
        var order = CreateLocalOrder();
        var now = DateTimeOffset.UtcNow;
        return order with
        {
            Payments =
            [
                new InstallmentPaymentDto(Guid.Parse("12345678-1111-2222-3333-444444444444"), PaymentMethodKind.Cash, 30m, "CASH-1", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                new InstallmentPaymentDto(Guid.Parse("12345678-3333-4444-5555-666666666666"), PaymentMethodKind.Voucher, 40m, "VOUCHER-1", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01")
            ],
            PaidAmount = 70m,
            BalanceAmount = 50m
        };
    }

    private static LocalInstallmentOrder CreateUnsupportedCancelOrder(string paymentScenario)
    {
        var order = CreateLocalOrder();
        var now = DateTimeOffset.UtcNow;
        var processor = paymentScenario == "Square" ? "Square" : "Linkly";
        var card = new InstallmentPaymentDto(
            Guid.Parse("12345678-2222-3333-4444-555555555555"),
            PaymentMethodKind.Card,
            40m,
            processor == "Square" ? "SQ:PAYMENT-1" : "ANZ:PAYMENT-1",
            InstallmentPaymentStatus.Recorded,
            now,
            "C001",
            "POS-01",
            [new CardTransactionDto(processor, "PAYMENT-1", null, null, null, null, null, "00", "APPROVED", null, now, 40m, null)]);
        var payments = paymentScenario == "Mixed"
            ? new List<InstallmentPaymentDto>
            {
                new(Guid.Parse("12345678-1111-2222-3333-444444444444"), PaymentMethodKind.Cash, 30m, "CASH-1", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                new(Guid.Parse("12345678-3333-4444-5555-666666666666"), PaymentMethodKind.Voucher, 20m, "VOUCHER-1", InstallmentPaymentStatus.Recorded, now, "C001", "POS-01"),
                card
            }
            : [card];
        return order with
        {
            Payments = payments,
            PaidAmount = payments.Sum(payment => payment.Amount),
            BalanceAmount = 50m
        };
    }

    private static InstallmentCancelResponse CreateCancelResponse(LocalInstallmentOrder order)
    {
        var details = CreateDetails(order.InstallmentGuid, InstallmentStatus.Cancelled) with
        {
            Payments = order.Payments,
            PaidAmount = order.PaidAmount,
            BalanceAmount = order.BalanceAmount,
            CancellationInfo = new InstallmentCancellationInfoDto(InstallmentCancellationKind.RefundCancel, DateTimeOffset.UtcNow, "Alice", "客户取消")
        };
        return new InstallmentCancelResponse(order.InstallmentGuid, InstallmentStatus.Cancelled, details, false, "已取消");
    }

    private static InstallmentDetailsDto CreateDetails(Guid installmentGuid, InstallmentStatus status) => new(
        installmentGuid, "IO-001", "S001", "POS-01", "C001", "Alice", "张三", "0400111222", DateTimeOffset.UtcNow,
        120m, 20m, 30m, 30m, 90m, status,
        [new InstallmentLineDto(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "SKU-001", null, "Rice Cooker", "690001", 1m, 120m, 0m, 120m)],
        [new InstallmentPaymentDto(Guid.Parse("12345678-1111-2222-3333-444444444444"), PaymentMethodKind.Cash, 30m, null, InstallmentPaymentStatus.Recorded, DateTimeOffset.UtcNow, "C001", "POS-01")],
        null, null, null);

    private static CardTransactionDto CreateCardTransaction(decimal amount) => new("Linkly", "CARD-1", "AUTH-1", "VISA", 4, "1234", "MID-1", "00", "APPROVED", "RRN-1", DateTimeOffset.UtcNow, amount, "receipt");
    private static string CreateTempDatabasePath() => Path.Combine(Path.GetTempPath(), $"hbpos-installment-service-{Guid.NewGuid():N}.db");

    private static void DeleteTempDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private sealed class CountingTerminal(bool approve) : ICardTerminalClient
    {
        public int AuthorizeCalls { get; private set; }
        public int RefundCalls { get; private set; }
        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(approve, "CARD-1", AuthorizedAmount: amount, CardTransactions: approve ? [CreateCardTransaction(amount)] : null, Processor: "Linkly"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(approve, $"REFUND:{originalReference}", AuthorizedAmount: amount, CardTransactions: approve ? [CreateCardTransaction(amount)] : null, Processor: "Linkly"));
        }
    }

    private sealed class CountingVoucherTenderClient(
        Func<int, decimal, PaymentAuthorizationResult>? refundFactory = null,
        Func<bool>? claimBegun = null) : IVoucherTenderClient
    {
        public int IssueRefundCalls { get; private set; }
        public bool ClaimWasBegunBeforeRefund { get; private set; }

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueRefundCalls++;
            ClaimWasBegunBeforeRefund = claimBegun?.Invoke() ?? false;
            return Task.FromResult(
                refundFactory?.Invoke(IssueRefundCalls, amount) ??
                new PaymentAuthorizationResult(true, $"VOUCHER-REFUND:{idempotencyKey}", AuthorizedAmount: amount));
        }

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class DurableActionOrderingApi(
        IInstallmentApiClient inner,
        LocalInstallmentOperationRepository repository,
        List<string> events) : IInstallmentApiClient
    {
        public Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            inner.GetRepaymentCapabilitiesAsync(cancellationToken);

        public async Task<InstallmentRepaymentClaimDto> CreateRepaymentClaimAsync(
            Guid installmentGuid,
            InstallmentRepaymentClaimCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            var operation = await repository.GetAsync(request.OperationGuid, cancellationToken);
            Assert.NotNull(operation);
            Assert.Equal(LocalInstallmentOperationState.Prepared, operation.State);
            events.Add("action:persisted");
            events.Add("claim:create");
            return await inner.CreateRepaymentClaimAsync(installmentGuid, request, cancellationToken);
        }

        public async Task<InstallmentRepaymentClaimDto> BeginRepaymentProviderAsync(
            Guid installmentGuid,
            Guid operationGuid,
            InstallmentRepaymentClaimBeginProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add($"claim:begin:{request.Provider}");
            return await inner.BeginRepaymentProviderAsync(installmentGuid, operationGuid, request, cancellationToken);
        }

        public Task<InstallmentRepaymentClaimDto> GetRepaymentClaimAsync(
            Guid installmentGuid,
            Guid operationGuid,
            CancellationToken cancellationToken = default) =>
            inner.GetRepaymentClaimAsync(installmentGuid, operationGuid, cancellationToken);

        public Task<InstallmentRepaymentClaimDto> ResolveRepaymentClaimAsync(
            Guid installmentGuid,
            Guid operationGuid,
            InstallmentRepaymentClaimResolveRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ResolveRepaymentClaimAsync(installmentGuid, operationGuid, request, cancellationToken);

        public async Task<InstallmentRepaymentClaimDto> CommitRepaymentClaimAsync(
            Guid installmentGuid,
            Guid operationGuid,
            InstallmentRepaymentClaimCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("claim:commit");
            return await inner.CommitRepaymentClaimAsync(installmentGuid, operationGuid, request, cancellationToken);
        }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(request, cancellationToken);

        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) =>
            inner.AppendPaymentAsync(request, cancellationToken);

        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) =>
            inner.ConfirmPickupAsync(request, cancellationToken);

        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(request, cancellationToken);

        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) =>
            inner.VoidAsync(request, cancellationToken);
    }

    private sealed class RepaymentOrderingTerminal(string? processor, List<string> events) : ICardTerminalClient
    {
        public int AuthorizeCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            events.Add($"provider:{processor}");
            var transaction = new CardTransactionDto(
                processor ?? "Unknown",
                processor == "Square" ? "square-payment-1" : "linkly-payment-1",
                "AUTH-1",
                "VISA",
                4,
                "1234",
                "MID-1",
                "00",
                "APPROVED",
                "RRN-1",
                DateTimeOffset.UtcNow,
                amount,
                null);
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                processor == "Square" ? "SQ:square-payment-1" : "ANZ:linkly-payment-1",
                AuthorizedAmount: amount,
                CardTransactions: [transaction],
                Processor: processor));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PersistedLinklyPurchaseTerminal(
        LocalCardPaymentAttemptRepository attempts,
        ILinklyPaymentAttemptContextAccessor context) : ICardTerminalClient
    {
        public Guid? AttemptGuid { get; private set; }

        public string? SubmittedTxnRef { get; private set; }

        public async Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            var current = context.Current ?? throw new InvalidOperationException("Linkly attempt context was not available.");
            var persisted = await attempts.GetAttemptAsync(current.AttemptGuid, cancellationToken);
            Assert.NotNull(persisted);
            Assert.Equal(LocalCardPaymentAttemptStatus.Pending, persisted.Status);
            Assert.Equal(persisted.TxnRef, current.TxnRef);
            AttemptGuid = current.AttemptGuid;
            SubmittedTxnRef = current.TxnRef;
            return new PaymentAuthorizationResult(
                true,
                $"ANZ:{SubmittedTxnRef}",
                "APPROVED",
                amount,
                [new CardTransactionDto("Linkly", SubmittedTxnRef, null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, amount, null)],
                "Linkly",
                "Sandbox",
                LinklyConnectionMode.LocalIp.ToString(),
                "P",
                null,
                SubmittedTxnRef,
                "00",
                "APPROVED");
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RepaymentOrderingVoucherClient(List<string> events) : IVoucherTenderClient
    {
        public int RedeemCalls { get; private set; }

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            RedeemCalls++;
            events.Add("provider:Voucher");
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                $"VOUCHER:{voucherCode}:LOCK-1",
                AuthorizedAmount: amount));
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ReleaseAsync(
            PosSessionState session,
            string voucherCode,
            string reservationToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class ClaimOrderingRefundTerminal(Func<bool> claimBegun) : ICardTerminalClient
    {
        public int RefundCalls { get; private set; }
        public bool ClaimWasBegunBeforeRefund { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            ClaimWasBegunBeforeRefund = claimBegun();
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                $"REFUND:{originalReference}",
                AuthorizedAmount: amount,
                CardTransactions: [CreateCardTransaction(amount)],
                Processor: "Linkly"));
        }
    }

    private sealed class UnknownRefundTerminal : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false, null, "network lost", ResultUnknown: true, Processor: "Linkly"));
    }

    private sealed class PendingSquareRefundTerminal(
        ISquarePaymentAttemptContextAccessor context,
        CardTerminalEnvironment environment) : ICardTerminalClient, IIdempotentCardRefundClient
    {
        public int RefundCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            RefundAsync(amount, session, originalReference, null, cancellationToken);

        public async Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string? idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            var attempt = context.Current ?? throw new InvalidOperationException("Square refund context was not available.");
            await BindRefundEvidenceAsync(attempt, "refund-pending", "PENDING", environment);
            return new PaymentAuthorizationResult(
                false,
                "SQRF:refund-pending",
                "Square refund is pending.",
                amount,
                [new CardTransactionDto("Square", "refund-pending", null, null, null, null, null, null, "PENDING", null, DateTimeOffset.UtcNow, amount, null)],
                Processor: "Square",
                ResponseText: "PENDING",
                ResultUnknown: true);
        }
    }

    private sealed class GatedSquareRefundTerminal(
        ISquarePaymentAttemptContextAccessor context,
        CardTerminalEnvironment environment) : ICardTerminalClient, IIdempotentCardRefundClient
    {
        public TaskCompletionSource RefundStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefundCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            RefundAsync(amount, session, originalReference, null, cancellationToken);

        public async Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string? idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            var attempt = context.Current ?? throw new InvalidOperationException("Square refund context was not available.");
            RefundStarted.TrySetResult();
            await ReleaseCallback.Task.WaitAsync(cancellationToken);
            await BindRefundEvidenceAsync(attempt, "refund-pending", "PENDING", environment);
            return new PaymentAuthorizationResult(
                false,
                "SQRF:refund-pending",
                "Square refund is pending.",
                amount,
                [new CardTransactionDto("Square", "refund-pending", null, null, null, null, null, null, "PENDING", null, DateTimeOffset.UtcNow, amount, null)],
                Processor: "Square",
                ResponseText: "PENDING",
                ResultUnknown: true);
        }
    }

    private sealed class RejectedSquareRefundTerminal(
        ISquarePaymentAttemptContextAccessor context,
        CardTerminalEnvironment environment) : ICardTerminalClient, IIdempotentCardRefundClient
    {
        public List<string> IdempotencyKeys { get; } = [];

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default) =>
            RefundAsync(amount, session, originalReference, null, cancellationToken);

        public async Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            string? idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            IdempotencyKeys.Add(Assert.IsType<string>(idempotencyKey));
            var attempt = context.Current ?? throw new InvalidOperationException("Square refund context was not available.");
            await BindRefundEvidenceAsync(attempt, "refund-rejected", "REJECTED", environment);
            return new PaymentAuthorizationResult(
                false,
                "SQRF:refund-rejected",
                "Square refund status is REJECTED.",
                Processor: "Square",
                ResponseText: "REJECTED");
        }
    }

    private static Task BindRefundEvidenceAsync(
        SquarePaymentAttemptContext attempt,
        string refundId,
        string status,
        CardTerminalEnvironment environment)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        return attempt.BindRefundEvidenceAsync is not null
            ? attempt.BindRefundEvidenceAsync(refundId, status, updatedAt, environment, CancellationToken.None)
            : attempt.BindRefundAsync!(refundId, status, updatedAt, CancellationToken.None);
    }

    private sealed class ThrowingCardTerminalSettingsProvider : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("settings unavailable");
    }

    private sealed class RefundStepCasWinnerSettingsProvider(
        LocalInstallmentOperationRepository repository,
        string storeCode) : ICardTerminalSettingsProvider
    {
        public const string WinnerReference = "RWINNER-CAS-01";
        public int GetSettingsCalls { get; private set; }

        public async Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            GetSettingsCalls++;
            var operation = Assert.Single(await repository.GetRecoverableAsync(storeCode, CancellationToken.None));
            var step = Assert.Single((await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None))
                .Where(candidate => candidate.Method == PaymentMethodKind.Card));
            Assert.True(await repository.TryTransitionRefundStepAsync(
                step.RefundStepGuid,
                [LocalInstallmentRefundStepState.Prepared],
                LocalInstallmentRefundStepState.TerminalSubmitting,
                DateTimeOffset.UtcNow,
                refundReference: WinnerReference,
                cancellationToken: CancellationToken.None));
            return CardTerminalSettings.FromEnvironment() with
            {
                Processor = CardProcessorKind.Linkly,
                LinklyConnectionMode = LinklyConnectionMode.LocalIp
            };
        }
    }

    private sealed class MutableSquareRefundStatusClient(SquareRefundStatusResult result) : ISquareTerminalPaymentClient
    {
        public SquareRefundStatusResult Result { get; set; } = result;
        public int GetRefundCalls { get; private set; }
        public List<string> RequestedRefundIds { get; } = [];
        public List<CardTerminalEnvironment> RequestedEnvironments { get; } = [];

        public Task<SquareCheckoutStatusResult> GetCheckoutAsync(CardTerminalSettings settings, string checkoutId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SquarePaymentStatusResult> GetPaymentAsync(CardTerminalSettings settings, string paymentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SquareRefundStatusResult> GetRefundAsync(CardTerminalSettings settings, string refundId, CancellationToken cancellationToken = default)
        {
            GetRefundCalls++;
            RequestedRefundIds.Add(refundId);
            RequestedEnvironments.Add(settings.Environment);
            return Task.FromResult(Result);
        }
    }

    private sealed class MissingAuthorizedAmountTerminal : ICardTerminalClient
    {
        public int AuthorizeCalls { get; private set; }
        public int RefundCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(true, "CARD-1", CardTransactions: [CreateCardTransaction(amount)], Processor: "Linkly"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", CardTransactions: [CreateCardTransaction(amount)], Processor: "Linkly"));
        }
    }

    private sealed class DeclineThenApproveRefundTerminal : ICardTerminalClient
    {
        public int RefundCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            return Task.FromResult(RefundCalls == 1
                ? new PaymentAuthorizationResult(false, null, "DECLINED", Processor: "Linkly", ResponseCode: "05", ResponseText: "DECLINED")
                : new PaymentAuthorizationResult(true, "REFUND-1", AuthorizedAmount: amount, CardTransactions: [CreateCardTransaction(amount)], Processor: "Linkly"));
        }
    }

    private sealed class RemoteApprovalTerminal(decimal amount) : ICardTerminalClient, IInstallmentTerminalRecoveryClient
    {
        public int AuthorizeCalls { get; private set; }
        public int RecoveryCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(false));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RecoverLinklyAsync(LocalCardPaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                Reference: "ANZ:LINKLY-REMOTE",
                AuthorizedAmount: amount,
                CardTransactions: [new CardTransactionDto("ANZ", "LINKLY-REMOTE", null, null, null, null, null, "00", "APPROVED", null, DateTimeOffset.UtcNow, amount, null)],
                Processor: "ANZ"));
        }

        public Task<PaymentAuthorizationResult> RecoverSquareAsync(LocalSquarePaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false, ResultUnknown: true));
    }

    private sealed class RemoteSquareApprovalTerminal(decimal amount) : ICardTerminalClient, IInstallmentTerminalRecoveryClient
    {
        public int AuthorizeCalls { get; private set; }
        public int RecoveryCalls { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(false));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RecoverLinklyAsync(LocalCardPaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false, ResultUnknown: true));

        public Task<PaymentAuthorizationResult> RecoverSquareAsync(LocalSquarePaymentAttempt attempt, PosSessionState session, CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            return Task.FromResult(new PaymentAuthorizationResult(
                true,
                Reference: "SQ:payment-001",
                AuthorizedAmount: amount,
                CardTransactions: [new CardTransactionDto("Square", "payment-001", null, null, null, null, null, null, "COMPLETED", null, DateTimeOffset.UtcNow, amount, null)],
                Processor: "Square"));
        }
    }

    private sealed class SequencedLinklyRefundTerminal(LocalInstallmentOperationRepository repository) : ICardTerminalClient, IIdempotentCardRefundClient
    {
        public int RefundCalls { get; private set; }
        public List<string?> RetryReferences { get; } = [];
        public List<string?> PersistedReferencesAtRetry { get; } = [];
        public List<string?> SubmissionReferences { get; } = [];
        public List<string?> PersistedReferencesAtSubmission { get; } = [];
        public List<string> PersistedIdempotencyKeysAtSubmission { get; } = [];

        public Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            RefundAsync(amount, session, originalReference, "legacy", cancellationToken);

        public async Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, string? idempotencyKey, CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            SubmissionReferences.Add(idempotencyKey);
            var persistedStep = await ReadPersistedStepAsync(session, cancellationToken);
            PersistedReferencesAtSubmission.Add(persistedStep.RefundReference);
            PersistedIdempotencyKeysAtSubmission.Add(persistedStep.IdempotencyKey);
            if (RefundCalls <= 2)
            {
                if (RefundCalls == 2)
                {
                    RetryReferences.Add(idempotencyKey);
                    PersistedReferencesAtRetry.Add(await ReadPersistedReferenceAsync(session, cancellationToken));
                }
                return new PaymentAuthorizationResult(false, null, "timeout", ResultUnknown: true);
            }

            RetryReferences.Add(idempotencyKey);
            PersistedReferencesAtRetry.Add(await ReadPersistedReferenceAsync(session, cancellationToken));
            return new PaymentAuthorizationResult(true, $"REFUND:{idempotencyKey}", AuthorizedAmount: amount, CardTransactions: [CreateCardTransaction(amount)], Processor: "Linkly");
        }

        private async Task<string?> ReadPersistedReferenceAsync(PosSessionState session, CancellationToken cancellationToken)
        {
            return (await ReadPersistedStepAsync(session, cancellationToken)).RefundReference;
        }

        private async Task<LocalInstallmentRefundStep> ReadPersistedStepAsync(PosSessionState session, CancellationToken cancellationToken)
        {
            var operation = Assert.Single(await repository.GetRecoverableAsync(session.StoreCode, cancellationToken));
            return Assert.Single((await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken))
                .Where(step => step.Method == PaymentMethodKind.Card));
        }
    }

    private sealed class CancelledTokenSessionBindingTerminal(ILinklyPaymentAttemptContextAccessor context) : ICardTerminalClient
    {
        public bool BindCalled { get; private set; }

        public async Task<PaymentAuthorizationResult> AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken = default)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var current = context.Current ?? throw new InvalidOperationException("Linkly attempt context was not available.");
            await current.BindSessionAsync("LINKLY-SESSION-001", "LINKLY-TXN-001", DateTimeOffset.UtcNow, cancelled.Token);
            BindCalled = true;
            return new PaymentAuthorizationResult(false, null, "connection lost", ResultUnknown: true);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, string? originalReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PaymentAuthorizationResult(false));
    }

    private sealed class NoopVoucherTenderClient : IVoucherTenderClient
    {
        public Task<PaymentAuthorizationResult> RedeemAsync(decimal amount, PosSessionState session, string? voucherCode, CancellationToken cancellationToken = default) => Task.FromResult(new PaymentAuthorizationResult(false));
        public Task<PaymentAuthorizationResult> IssueRefundAsync(decimal amount, PosSessionState session, string orderReference, string idempotencyKey, string? reason = null, CancellationToken cancellationToken = default) => Task.FromResult(new PaymentAuthorizationResult(false));
        public Task<bool> ReleaseAsync(PosSessionState session, string voucherCode, string reservationToken, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingInstallmentApi(Dictionary<Guid, InstallmentCancelClaimDto>? cancelClaims = null) : IInstallmentApiClient
    {
        public Exception? AppendException { get; init; }
        public InstallmentAppendPaymentResponse? AppendResponse { get; init; }
        public Exception? CancelException { get; init; }
        public InstallmentCancelResponse? CancelResponse { get; init; }
        public Exception? PickupException { get; init; }
        public InstallmentConfirmPickupResponse? PickupResponse { get; init; }
        public TaskCompletionSource? AppendGate { get; init; }
        public TaskCompletionSource AppendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AppendCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int PickupCalls { get; private set; }
        public int CreateCancelClaimCalls { get; private set; }
        public int BeginCancelRefundCalls { get; private set; }
        public int GetCancelClaimCalls { get; private set; }
        public int CommitCancelClaimCalls { get; private set; }
        public Exception? CancelClaimCreateException { get; init; }
        public Exception? CancelClaimBeginException { get; init; }
        public Exception? CancelClaimGetException { get; init; }
        public Exception? CancelClaimResolveException { get; init; }
        public int DeclinedCancelResolveFailuresRemaining { get; set; }
        public InstallmentCancelClaimStatus? CancelClaimStatusAfterBeginException { get; init; }
        public bool LoseFirstCancelCommitResponse { get; init; }
        public Exception? CancelClaimCommitException { get; init; }
        public List<Guid> CreatedCancelOperationGuids { get; } = [];
        public List<InstallmentCancelClaimResolveOutcome> CancelResolveOutcomes { get; } = [];
        public List<InstallmentCancelClaimResolveRequest> CancelResolveRequests { get; } = [];
        public InstallmentCancelClaimCreateRequest? LastCancelClaimCreateRequest { get; private set; }
        public InstallmentCancelClaimCommitRequest? LastCancelClaimCommitRequest { get; private set; }
        public InstallmentAppendPaymentRequest? LastAppendRequest { get; private set; }
        public InstallmentConfirmPickupRequest? LastPickupRequest { get; private set; }
        private readonly Dictionary<Guid, InstallmentCancelClaimDto> _cancelClaims = cancelClaims ?? [];

        public InstallmentCreateResponse? CreateResponse { get; init; }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResponse ?? throw new NotSupportedException());

        public async Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            AppendCalls++;
            LastAppendRequest = request;
            AppendStarted.TrySetResult();
            if (AppendGate is not null) await AppendGate.Task;
            if (AppendException is not null) throw AppendException;
            return AppendResponse ?? throw new NotSupportedException();
        }

        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            PickupCalls++;
            LastPickupRequest = request;
            if (PickupException is not null)
            {
                return Task.FromException<InstallmentConfirmPickupResponse>(PickupException);
            }

            return Task.FromResult(PickupResponse ?? throw new NotSupportedException());
        }

        public Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallmentRepaymentCapabilitiesResponse(true, false, false, 120, true, false, 120));

        public Task<InstallmentCancelClaimDto> CreateCancelClaimAsync(Guid installmentGuid, InstallmentCancelClaimCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreateCancelClaimCalls++;
            CreatedCancelOperationGuids.Add(request.OperationGuid);
            LastCancelClaimCreateRequest = request;
            if (CancelClaimCreateException is not null)
            {
                return Task.FromException<InstallmentCancelClaimDto>(CancelClaimCreateException);
            }

            if (_cancelClaims.TryGetValue(request.OperationGuid, out var existing))
            {
                return Task.FromResult(existing with { AlreadyExists = true });
            }

            var now = DateTimeOffset.UtcNow;
            var claim = new InstallmentCancelClaimDto(
                installmentGuid,
                request.OperationGuid,
                request.IdempotencyKey,
                request.RefundPlanFingerprint,
                InstallmentCancelClaimStatus.Prepared,
                now,
                now,
                now.AddSeconds(120),
                RefundMode: request.RefundMode ?? InstallmentCancelRefundMode.OriginalRoute);
            _cancelClaims.Add(request.OperationGuid, claim);
            return Task.FromResult(claim);
        }

        public Task<InstallmentCancelClaimDto> BeginCancelRefundAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default)
        {
            BeginCancelRefundCalls++;
            if (CancelClaimBeginException is not null)
            {
                if (CancelClaimStatusAfterBeginException is { } status)
                {
                    _cancelClaims[operationGuid] = _cancelClaims[operationGuid] with
                    {
                        Status = status,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        ExpiresAtUtc = null
                    };
                }
                return Task.FromException<InstallmentCancelClaimDto>(CancelClaimBeginException);
            }

            var claim = _cancelClaims[operationGuid] with
            {
                Status = InstallmentCancelClaimStatus.RefundPending,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = null
            };
            _cancelClaims[operationGuid] = claim;
            return Task.FromResult(claim);
        }

        public Task<InstallmentCancelClaimDto> GetCancelClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default)
        {
            GetCancelClaimCalls++;
            if (CancelClaimGetException is not null)
            {
                return Task.FromException<InstallmentCancelClaimDto>(CancelClaimGetException);
            }
            return _cancelClaims.TryGetValue(operationGuid, out var claim)
                ? Task.FromResult(claim with { AlreadyExists = true })
                : Task.FromException<InstallmentCancelClaimDto>(new CatalogApiException("not found", System.Net.HttpStatusCode.NotFound, "INSTALLMENT_CANCEL_CLAIM_NOT_FOUND"));
        }

        public Task<InstallmentCancelClaimDto> ResolveCancelClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimResolveRequest request, CancellationToken cancellationToken = default)
        {
            CancelResolveOutcomes.Add(request.Outcome);
            CancelResolveRequests.Add(request);
            if (request.Outcome == InstallmentCancelClaimResolveOutcome.Declined && DeclinedCancelResolveFailuresRemaining > 0)
            {
                DeclinedCancelResolveFailuresRemaining--;
                return Task.FromException<InstallmentCancelClaimDto>(new HttpRequestException("temporary resolve failure"));
            }
            if (CancelClaimResolveException is not null)
            {
                return Task.FromException<InstallmentCancelClaimDto>(CancelClaimResolveException);
            }
            var status = request.Outcome switch
            {
                InstallmentCancelClaimResolveOutcome.Released => InstallmentCancelClaimStatus.Released,
                InstallmentCancelClaimResolveOutcome.Declined => InstallmentCancelClaimStatus.Declined,
                _ => InstallmentCancelClaimStatus.Unknown
            };
            var claim = _cancelClaims[operationGuid] with { Status = status, UpdatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = null };
            _cancelClaims[operationGuid] = claim;
            return Task.FromResult(claim);
        }

        public Task<InstallmentCancelClaimDto> CommitCancelClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimCommitRequest request, CancellationToken cancellationToken = default)
        {
            CommitCancelClaimCalls++;
            LastCancelClaimCommitRequest = request;
            if (CancelClaimCommitException is not null)
            {
                return Task.FromException<InstallmentCancelClaimDto>(CancelClaimCommitException);
            }
            var response = CancelResponse ?? throw new NotSupportedException();
            var claim = _cancelClaims[operationGuid] with
            {
                Status = InstallmentCancelClaimStatus.Committed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = null,
                Commit = new InstallmentCancelClaimCommitResponse(response.Details, response.AlreadyCancelled)
            };
            _cancelClaims[operationGuid] = claim;
            if (LoseFirstCancelCommitResponse && CommitCancelClaimCalls == 1)
            {
                return Task.FromException<InstallmentCancelClaimDto>(new HttpRequestException("response lost"));
            }
            return Task.FromResult(claim);
        }

        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (CancelException is not null) return Task.FromException<InstallmentCancelResponse>(CancelException);
            return Task.FromResult(CancelResponse ?? throw new NotSupportedException());
        }

        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
