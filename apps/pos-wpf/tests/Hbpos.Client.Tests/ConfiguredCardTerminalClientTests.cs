using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Tests;

public sealed class ConfiguredCardTerminalClientTests
{
    private const string InitialToken = "opaque-initial-square-token";
    private const string RefreshedToken = "opaque-refreshed-square-token";
    private static readonly Uri HbposApiBaseAddress = new("http://localhost:5159/");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Manual_card_refund_never_routes_to_configured_terminal(bool square, bool wrapped)
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(square ? CreateSquareSettings() : CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP must not be called.")))));
        var reference = ManualCardPaymentReference.Format(Guid.NewGuid());
        if (wrapped) reference = CardRefundReference.Format("", reference);
        var result = await client.RefundAsync(10m, CreateSession(), reference);
        Assert.False(result.Approved);
        Assert.Equal("Manual card payments cannot be refunded through a linked terminal.", result.Message);
    }

    [Fact]
    public async Task Linkly_authorization_holds_selection_gate_until_terminal_call_completes()
    {
        var completion = new TaskCompletionSource<PaymentAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "approved"))
        {
            PurchaseCompletion = completion
        };
        using var gate = new LinklyTerminalSelectionTransitionGate();
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal,
            linklyTerminalSelectionTransitionGate: gate);

        var authorization = client.AuthorizeAsync(10m, CreateSession());
        await terminal.PurchaseStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Null(await gate.TryEnterAssignmentAsync());
        completion.SetResult(new PaymentAuthorizationResult(true, "approved"));
        Assert.True((await authorization).Approved);
        await using var assignmentLease = await gate.TryEnterAssignmentAsync();
        Assert.NotNull(assignmentLease);
    }

    [Fact]
    public async Task AuthorizeAsync_fails_when_card_terminal_is_not_configured()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.None }),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Card terminal is not configured.", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("P1234567890123456")]
    [InlineData("P1234\n5678")]
    [InlineData("P交易")]
    public async Task RecoverLinklyAsync_rejects_invalid_local_historical_txn_ref_before_get_last(
        string txnRef)
    {
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZ:P000000000000001",
            AuthorizedAmount: 10m,
            TxnType: "P",
            TxnRef: "P000000000000001"));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.True(result.ResultUnknown);
        Assert.Equal(0, terminal.RecoverCallCount);
    }

    [Fact]
    public async Task RecoverLinklyAsync_normalizes_anz_wrapper_and_protocol_padding_before_exact_match()
    {
        const string txnRef = "P000000000000201";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            $"ANZ:{txnRef}   ",
            AuthorizedAmount: 10m,
            TxnType: "P",
            TxnRef: $"{txnRef}   "));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt($" ANZ:{txnRef}   "),
            CreateSession());

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(txnRef, terminal.LastRecoveryTxnRef);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("transaction-reference")]
    [InlineData("transaction-amount")]
    public async Task RecoverLinklyAsync_any_returned_identity_or_nonzero_amount_conflict_stays_unknown(
        string conflict)
    {
        const string txnRef = "P000000000000208";
        const string conflictingTxnRef = "P000000000000209";
        var returnedReference = string.Equals(conflict, "reference", StringComparison.Ordinal)
            ? $"ANZ:{conflictingTxnRef}"
            : $"ANZ:{txnRef}";
        var transactionTxnRef = string.Equals(conflict, "transaction-reference", StringComparison.Ordinal)
            ? conflictingTxnRef
            : txnRef;
        var transactionAmount = string.Equals(conflict, "transaction-amount", StringComparison.Ordinal)
            ? 9m
            : 10m;
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            returnedReference,
            AuthorizedAmount: 10m,
            CardTransactions:
            [
                new CardTransactionDto(
                    "ANZ",
                    txnRef,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "00",
                    "APPROVED",
                    null,
                    null,
                    10m,
                    null),
                new CardTransactionDto(
                    "ANZ",
                    transactionTxnRef,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "00",
                    "APPROVED",
                    null,
                    null,
                    transactionAmount,
                    null)
            ],
            TxnType: "P",
            TxnRef: txnRef));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, terminal.RecoverCallCount);
    }

    [Theory]
    [InlineData("p000000000000202", "P")]
    [InlineData("P000000000000202", "R")]
    [InlineData("P00000000000020", "P")]
    public async Task RecoverLinklyAsync_approved_reference_or_txn_type_conflict_stays_unknown(
        string returnedTxnRef,
        string returnedTxnType)
    {
        const string persistedTxnRef = "P000000000000202";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            $"ANZ:{returnedTxnRef}",
            AuthorizedAmount: 10m,
            TxnType: returnedTxnType,
            TxnRef: returnedTxnRef));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(persistedTxnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, terminal.RecoverCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task RecoverLinklyAsync_approved_missing_or_zero_amount_stays_unknown(int? returnedAmount)
    {
        const string txnRef = "P000000000000203";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            $"ANZ:{txnRef}",
            AuthorizedAmount: returnedAmount,
            TxnType: "P",
            TxnRef: txnRef));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLinklyAsync_approved_amount_requires_exact_decimal_match()
    {
        const string txnRef = "P000000000000204";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            $"ANZ:{txnRef}",
            AuthorizedAmount: 10.0005m,
            TxnType: "P",
            TxnRef: txnRef));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(10)]
    public async Task RecoverLinklyAsync_final_decline_accepts_missing_zero_or_exact_amount(
        int? returnedAmount)
    {
        const string txnRef = "P000000000000205";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            false,
            $"ANZ:{txnRef}",
            "DECLINED",
            returnedAmount,
            TxnType: "P",
            TxnRef: txnRef,
            ResponseCode: "05",
            ResponseText: "DECLINED"));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("05", result.ResponseCode);
    }

    [Fact]
    public async Task RecoverLinklyAsync_final_decline_with_nonzero_amount_mismatch_stays_unknown()
    {
        const string txnRef = "P000000000000206";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            false,
            $"ANZ:{txnRef}",
            "DECLINED",
            9m,
            TxnType: "P",
            TxnRef: txnRef,
            ResponseCode: "05",
            ResponseText: "DECLINED"));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverLinklyAsync_provider_unknown_does_not_require_identity_fields()
    {
        const string txnRef = "P000000000000207";
        var terminal = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            false,
            Message: "Terminal outcome unavailable.",
            ResultUnknown: true));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateLinklySettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            terminal);

        var result = await client.RecoverLinklyAsync(
            CreateLinklyRecoveryAttempt(txnRef),
            CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Terminal outcome unavailable.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_fails_closed_for_linkly_when_adapter_is_unavailable()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly }),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Contains("Linkly terminal adapter is unavailable", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_delegates_linkly_purchase_to_adapter()
    {
        var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZ:TXN-1",
            "ANZ Linkly",
            10m,
            [new CardTransactionDto("ANZ", "TXN-1", "123456", "VISA", 4, "****1234", "MID", "00", "APPROVED", "42", DateTimeOffset.UtcNow, 10m, "receipt")]));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("ANZ:TXN-1", result.Reference);
        Assert.Equal(10m, linkly.LastAmount);
        Assert.Equal("TXN-1", Assert.Single(result.CardTransactions!).TxnRef);
    }

    [Fact]
    public async Task AuthorizeAsync_uses_recoverable_txn_ref_for_local_ip_linkly_purchase()
    {
        var settings = CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.LocalIp
        };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "ANZ:LOCAL-TXN-001", AuthorizedAmount: 10m));
        var attemptContext = new LinklyPaymentAttemptContextAccessor();
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly,
            linklyPaymentAttemptContextAccessor: attemptContext);
        using var scope = attemptContext.Begin(new LinklyPaymentAttemptContext(
            Guid.NewGuid(),
            (_, _, _, _) => Task.CompletedTask,
            "LOCAL-TXN-001"));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("LOCAL-TXN-001", linkly.LastPurchaseReference);
    }

    [Fact]
    public async Task AuthorizeAsync_uses_attempt_settings_snapshot_for_route_and_txn_ref()
    {
        const string txnRef = "P000000000000210";
        var snapshot = CreateLinklySettings();
        var current = snapshot with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            $"ANZ:{txnRef}",
            AuthorizedAmount: 10m,
            TxnType: "P",
            TxnRef: txnRef));
        var attemptContext = new LinklyPaymentAttemptContextAccessor();
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(current),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly,
            linklyPaymentAttemptContextAccessor: attemptContext);
        using var scope = attemptContext.Begin(new LinklyPaymentAttemptContext(
            Guid.NewGuid(),
            (_, _, _, _) => Task.CompletedTask,
            txnRef)
        {
            SettingsSnapshot = snapshot
        });

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(txnRef, linkly.LastPurchaseReference);
        Assert.Equal(LinklyConnectionMode.LocalIp, linkly.LastSettings?.LinklyConnectionMode);
    }

    [Fact]
    public async Task Bound_authorize_uses_explicit_settings_without_reading_provider()
    {
        var explicitSettings = CreateLinklySettings();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateSquareSettings(),
            CreateSquareSettings());
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZ:BOUND-AUTH",
            AuthorizedAmount: 10m));
        var client = new ConfiguredCardTerminalClient(
            settingsProvider,
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await ((ICardTerminalSettingsBoundClient)client).AuthorizeWithSettingsAsync(
            explicitSettings,
            10m,
            CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(0, settingsProvider.GetSettingsCalls);
        Assert.Equal(10m, linkly.LastAmount);
        Assert.Equal(CardProcessorKind.Linkly, linkly.LastSettings?.Processor);
    }

    [Fact]
    public async Task Bound_refund_uses_explicit_settings_without_reading_provider()
    {
        var explicitSettings = CreateLinklySettings();
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateSquareSettings(),
            CreateSquareSettings());
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZ:BOUND-REFUND",
            AuthorizedAmount: 6m));
        var client = new ConfiguredCardTerminalClient(
            settingsProvider,
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await ((ICardTerminalSettingsBoundClient)client).RefundWithSettingsAsync(
            explicitSettings,
            6m,
            CreateSession(),
            "ANZ:ORIGINAL-BOUND",
            "R000000000000001");

        Assert.True(result.Approved);
        Assert.Equal(0, settingsProvider.GetSettingsCalls);
        Assert.Equal(6m, linkly.LastRefundAmount);
        Assert.Equal("ANZ:ORIGINAL-BOUND", linkly.LastOriginalReference);
        Assert.Equal("R000000000000001", linkly.LastRefundTransactionReference);
        Assert.Equal(CardProcessorKind.Linkly, linkly.LastSettings?.Processor);
    }

    [Fact]
    public async Task Bound_authorize_routes_explicit_square_settings_without_reading_provider()
    {
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateLinklySettings(),
            CreateLinklySettings());
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse("""
                    { "checkout": { "id": "checkout-bound-square", "status": "PENDING" } }
                    """);
            }

            if (request.RequestUri!.AbsolutePath.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "payment": {
                        "id": "payment-bound-square",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "checkout": {
                    "id": "checkout-bound-square",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-bound-square" ]
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(settingsProvider, CreateApiClient(handler));

        var result = await ((ICardTerminalSettingsBoundClient)client).AuthorizeWithSettingsAsync(
            CreateSquareSettings(),
            5m,
            CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("SQ:payment-bound-square", result.Reference);
        Assert.Equal(0, settingsProvider.GetSettingsCalls);
    }

    [Fact]
    public async Task Public_authorize_still_reads_current_settings_once()
    {
        var settingsProvider = new TxnRefSequencedCardTerminalSettingsProvider(
            CreateLinklySettings(),
            CreateSquareSettings());
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(
            true,
            "ANZ:PUBLIC-AUTH",
            AuthorizedAmount: 10m));
        var client = new ConfiguredCardTerminalClient(
            settingsProvider,
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(1, settingsProvider.GetSettingsCalls);
        Assert.Equal(10m, linkly.LastAmount);
    }

    [Fact]
    public async Task RefundAsync_delegates_linkly_refund_to_adapter()
    {
        var settings = CardTerminalSettings.FromEnvironment() with { Processor = CardProcessorKind.Linkly };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "ANZ:REFUND-1", AuthorizedAmount: 6m));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.RefundAsync(6m, CreateSession(), "ANZ:ORIGINAL-1");

        Assert.True(result.Approved);
        Assert.Equal("ANZ:REFUND-1", result.Reference);
        Assert.Equal(6m, linkly.LastRefundAmount);
        Assert.Equal("ANZ:ORIGINAL-1", linkly.LastOriginalReference);
    }

    [Fact]
    public async Task Idempotent_refund_uses_persisted_linkly_refund_txn_ref_without_replacing_original_reference()
    {
        var settings = CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.LocalIp
        };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "ANZ:REFUND-1", AuthorizedAmount: 6m));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await ((IIdempotentCardRefundClient)client).RefundAsync(
            6m,
            CreateSession(),
            "ANZ:ORIGINAL-1",
            "REFUND-TXN-001");

        Assert.True(result.Approved);
        Assert.Equal("ANZ:ORIGINAL-1", linkly.LastOriginalReference);
        Assert.Equal("REFUND-TXN-001", linkly.LastRefundTransactionReference);
    }

    [Fact]
    public async Task AuthorizeAsync_preserves_linkly_backend_async_mode_for_configured_adapter()
    {
        var settings = CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync
        };
        var linkly = new StubLinklyTerminalClient(new PaymentAuthorizationResult(true, "ANZBACKEND:TXN-1", AuthorizedAmount: 10m));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))),
            linkly);

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, linkly.LastSettings?.LinklyConnectionMode);
    }

    [Fact]
    public async Task RefundAsync_treats_square_pending_refund_as_accepted_and_does_not_poll()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequests.Add(CloneRequestWithBody(request));
            return JsonResponse(CreateSquareRefundCreateJson("refund-1", "PENDING"));
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        // Square 官方定义 PENDING 为“已受理、正在退回原卡”（多数几小时内完成，最长 14 天），
        // 与 COMPLETED 一样视为退款已发起：退货单当场完成，由结算跟踪继续查询。
        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("SQRF:refund-1", result.Reference);
        Assert.Equal(12.34m, result.AuthorizedAmount);
        Assert.Equal("payment.card.squareRefundAccepted", result.StatusKey);
        Assert.Contains("accepted the refund", result.Message, StringComparison.Ordinal);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("PENDING", transaction.ResponseText);
        Assert.Equal("refund-1", transaction.TxnRef);
        // 只有一次 POST，没有任何轮询或二次请求。
        var request = Assert.Single(capturedRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        AssertHbposApiRequest(request, "api/v1/square/refunds");
        AssertNoSquareHeaders(request);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"paymentId\":\"payment-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"amount\":1234", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefundAsync_returns_plain_success_when_square_completes_the_refund_immediately()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            return JsonResponse(CreateSquareRefundCreateJson("refund-done", "COMPLETED"));
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("SQRF:refund-done", result.Reference);
        Assert.Equal("COMPLETED", result.Message);
        Assert.Null(result.StatusKey);
        Assert.Equal("COMPLETED", Assert.Single(result.CardTransactions!).ResponseText);
        Assert.Equal(1, requestCount);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("REJECTED")]
    public async Task RefundAsync_returns_square_refund_failure_when_square_rejects_immediately(string status)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(CreateSquareRefundCreateJson("refund-failed", status)));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("SQRF:refund-failed", result.Reference);
        Assert.Equal($"Square refund status is {status}.", result.Message);
    }

    [Fact]
    public async Task RefundAsync_persists_refund_id_before_returning_the_accepted_result()
    {
        var persisted = new List<(string RefundId, string Status, CardTerminalEnvironment Environment, DateTimeOffset UpdatedAt)>();
        var contextAccessor = new SquarePaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(CreateSquareRefundCreateJson("refund-durable", "PENDING")));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squarePaymentAttemptContextAccessor: contextAccessor);
        using var scope = contextAccessor.Begin(new SquarePaymentAttemptContext(
            Guid.NewGuid(),
            "refund-idempotency",
            SubmissionToken: "refund-submission",
            BindRefundEvidenceAsync: (refundId, status, updatedAt, environment, _) =>
            {
                persisted.Add((refundId, status, environment, updatedAt));
                return Task.CompletedTask;
            }));

        var result = await ((IIdempotentCardRefundClient)client).RefundAsync(
            12.34m,
            CreateSession(),
            "SQ:payment-1",
            "refund-idempotency");

        Assert.True(result.Approved);
        // Square 返回 refundId 后必须先落库退款证据，结算跟踪靠它查询同一笔退款。
        var evidence = Assert.Single(persisted);
        Assert.Equal("refund-durable", evidence.RefundId);
        Assert.Equal("PENDING", evidence.Status);
        Assert.Equal(CardTerminalEnvironment.Production, evidence.Environment);
        Assert.NotEqual(default, evidence.UpdatedAt);
    }

    private static string CreateSquareRefundCreateJson(string refundId, string status) =>
        $$"""{ "refund": { "id": "{{refundId}}", "status": "{{status}}", "payment_id": "payment-1", "amount_money": { "amount": 1234, "currency": "AUD" } } }""";

    [Fact]
    public async Task Installment_refund_uses_the_persisted_square_idempotency_key_in_the_http_body()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                capturedRequest = CloneRequestWithBody(request);
                return JsonResponse("{ \"refund\": { \"id\": \"refund-persisted\", \"status\": \"PENDING\", \"payment_id\": \"payment-1\", \"amount_money\": { \"amount\": 1234, \"currency\": \"AUD\" } } }");
            }

            return JsonResponse("{ \"success\": true, \"data\": { \"refundId\": \"refund-persisted\", \"environment\": \"Production\", \"status\": \"COMPLETED\", \"paymentId\": \"payment-1\", \"amountMoney\": { \"amount\": 1234, \"currency\": \"AUD\" } } }");
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await ((IIdempotentCardRefundClient)client).RefundAsync(
            12.34m,
            CreateSession(),
            "SQ:payment-1",
            "persisted-installment-key");

        Assert.True(result.Approved);
        Assert.NotNull(capturedRequest);
        Assert.Equal("persisted-installment-key", ReadJsonString(await capturedRequest!.Content!.ReadAsStringAsync(), "idempotencyKey"));
    }

    [Fact]
    public async Task RefundAsync_marks_result_unknown_when_square_refund_amount_does_not_match()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) => JsonResponse(
                "{ \"refund\": { \"id\": \"refund-amount-mismatch\", \"status\": \"PENDING\", \"payment_id\": \"payment-1\", \"amount_money\": { \"amount\": 1233, \"currency\": \"AUD\" } } }"))));

        var result = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RefundAsync_reuses_square_refund_idempotency_key_after_network_failure()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var postCallCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("{ \"success\": true, \"data\": { \"refundId\": \"refund-retry-1\", \"environment\": \"Production\", \"status\": \"COMPLETED\", \"paymentId\": \"payment-1\", \"amountMoney\": { \"amount\": 1234, \"currency\": \"AUD\" } } }");
            }

            capturedRequests.Add(CloneRequestWithBody(request));
            postCallCount++;
            if (postCallCount == 1)
            {
                throw new HttpRequestException("Network dropped after Square accepted the refund.");
            }

            return JsonResponse(
                """
                {
                  "refund": {
                    "id": "refund-retry-1",
                    "status": "PENDING",
                    "payment_id": "payment-1",
                    "amount_money": { "amount": 1234, "currency": "AUD" }
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var first = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");
        var second = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.False(first.Approved);
        Assert.True(second.Approved);
        Assert.Equal(2, capturedRequests.Count);
        var firstKey = ReadJsonString(await capturedRequests[0].Content!.ReadAsStringAsync(), "idempotencyKey");
        var secondKey = ReadJsonString(await capturedRequests[1].Content!.ReadAsStringAsync(), "idempotencyKey");
        Assert.Equal(firstKey, secondKey);
    }

    [Fact]
    public async Task RefundAsync_reuses_square_refund_idempotency_key_after_backend_gateway_failure()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var postCallCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("{ \"success\": true, \"data\": { \"refundId\": \"refund-retry-1\", \"environment\": \"Production\", \"status\": \"COMPLETED\", \"paymentId\": \"payment-1\", \"amountMoney\": { \"amount\": 1234, \"currency\": \"AUD\" } } }");
            }

            capturedRequests.Add(CloneRequestWithBody(request));
            postCallCount++;
            if (postCallCount == 1)
            {
                return JsonResponse(
                    HttpStatusCode.BadGateway,
                    """
                    {
                      "success": false,
                      "errorCode": "SQUARE_UPSTREAM_REQUEST_FAILED",
                      "message": "Square upstream request failed."
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "refund": {
                    "id": "refund-retry-1",
                    "status": "PENDING",
                    "payment_id": "payment-1",
                    "amount_money": { "amount": 1234, "currency": "AUD" }
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var first = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");
        var second = await client.RefundAsync(12.34m, CreateSession(), "SQ:payment-1");

        Assert.False(first.Approved);
        Assert.True(second.Approved);
        Assert.Equal(2, capturedRequests.Count);
        var firstKey = ReadJsonString(await capturedRequests[0].Content!.ReadAsStringAsync(), "idempotencyKey");
        var secondKey = ReadJsonString(await capturedRequests[1].Content!.ReadAsStringAsync(), "idempotencyKey");
        Assert.Equal(firstKey, secondKey);
    }

    [Fact]
    public async Task RefundAsync_rejects_missing_square_payment_reference()
    {
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(new StubHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP should not be called.")))));

        var result = await client.RefundAsync(5m, CreateSession(), null);

        Assert.False(result.Approved);
        Assert.Equal("Square refund requires an original Square payment reference.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_completes_square_checkout_and_returns_payment_reference()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var statusPollCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Post)
            {
                requests[^1] = CloneRequestWithBody(request);
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-1",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-1",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 1099, "currency": "AUD" },
                        "card_details": {
                          "status": "AUTHORIZED",
                          "card": {
                            "card_brand": "VISA",
                            "last_4": "1111",
                            "bin": "411111",
                            "exp_month": 11,
                            "exp_year": 2030,
                            "fingerprint": "sq-test-fingerprint"
                          },
                          "auth_result_code": "68aLBM",
                          "entry_method": "CONTACTLESS"
                        }
                      }
                    }
                    """);
            }

            statusPollCount++;
            return JsonResponse(
                statusPollCount == 1
                    ? """
                      {
                        "checkout": {
                          "id": "checkout-1",
                          "status": "IN_PROGRESS"
                        }
                      }
                      """
                    : """
                      {
                        "checkout": {
                          "id": "checkout-1",
                          "status": "COMPLETED",
                          "amount_money": { "amount": 1099, "currency": "AUD" },
                          "payment_ids": [ "payment-1" ]
                        }
                      }
                      """);
        });
        var settings = CreateSquareSettings();
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(settings), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10.99m, CreateSession());

        Assert.True(result.Approved);
        Assert.Equal("SQ:payment-1", result.Reference);
        Assert.Equal(10.99m, result.AuthorizedAmount);
        var cardTransaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("Square", cardTransaction.Processor);
        Assert.Equal("payment-1", cardTransaction.TxnRef);
        Assert.Equal("VISA", cardTransaction.CardType);
        Assert.Equal("****1111", cardTransaction.MaskedCardNumber);
        Assert.Equal("68aLBM", cardTransaction.AuthCode);
        Assert.Collection(
            requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                AssertHbposApiRequest(create, "api/v1/square/checkouts");
                AssertNoSquareHeaders(create);
                Assert.Equal("DEV-1", ReadCheckoutDeviceId(create));
            },
            firstStatus =>
            {
                Assert.Equal(HttpMethod.Get, firstStatus.Method);
                AssertHbposApiRequest(firstStatus, "api/v1/square/checkouts/checkout-1?environment=Production");
                AssertNoSquareHeaders(firstStatus);
            },
            secondStatus =>
            {
                Assert.Equal(HttpMethod.Get, secondStatus.Method);
                AssertHbposApiRequest(secondStatus, "api/v1/square/checkouts/checkout-1?environment=Production");
                AssertNoSquareHeaders(secondStatus);
            },
            payment =>
            {
                Assert.Equal(HttpMethod.Get, payment.Method);
                AssertHbposApiRequest(payment, "api/v1/square/payments/payment-1?environment=Production");
                AssertNoSquareHeaders(payment);
            });
        AssertContainsLogLine(logs.Lines, "authorize start");
        AssertContainsLogLine(logs.Lines, "storedSquareDeviceId=DEV-1 checkoutDeviceId=DEV-1");
        AssertContainsLogLine(logs.Lines, "checkout create succeeded checkoutId=checkout-1 status=PENDING");
        AssertContainsLogLine(logs.Lines, "checkout status checkoutId=checkout-1 poll=1 status=IN_PROGRESS");
        AssertContainsLogLine(logs.Lines, "checkout status checkoutId=checkout-1 poll=2 status=COMPLETED");
        AssertContainsLogLine(logs.Lines, "checkout completed checkoutId=checkout-1 paymentId=payment-1 amount=10.99");
        AssertContainsLogLine(logs.Lines, "payment verified checkoutId=checkout-1 paymentId=payment-1 status=COMPLETED amount=10.99 currency=AUD");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_completed_square_checkout_without_payment_id()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-no-payment",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-no-payment",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": []
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square checkout did not return a payment id.", result.Message);
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("PENDING")]
    [InlineData("CANCELED")]
    [InlineData("FAILED")]
    public async Task AuthorizeAsync_rejects_square_payment_status_that_is_not_completed(string paymentStatus)
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "%STATUS%",
                "amount_money": { "amount": 500, "currency": "AUD" }
              }
            }
            """.Replace("%STATUS%", paymentStatus, StringComparison.Ordinal));
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal(
            !string.Equals(paymentStatus, "CANCELED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(paymentStatus, "FAILED", StringComparison.OrdinalIgnoreCase),
            result.ResultUnknown);
        Assert.Equal($"Square payment status is {paymentStatus}.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_square_payment_amount_mismatch()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "COMPLETED",
                "amount_money": { "amount": 499, "currency": "AUD" }
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square payment amount did not match the requested amount.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_square_payment_currency_mismatch()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "COMPLETED",
                "amount_money": { "amount": 500, "currency": "USD" }
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square payment currency did not match the requested currency.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_marks_attempt_unknown_when_polled_checkout_id_does_not_match_bound_checkout()
    {
        var attemptGuid = Guid.NewGuid();
        var context = new SquarePaymentAttemptContextAccessor();
        var repository = new RecordingFailureSquarePaymentAttemptRepository();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse("{ \"checkout\": { \"id\": \"checkout-expected\", \"status\": \"PENDING\" } }");
            }

            return JsonResponse("{ \"checkout\": { \"id\": \"checkout-other\", \"status\": \"PENDING\" } }");
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squarePaymentAttemptContextAccessor: context,
            squarePaymentAttemptRepository: repository);

        PaymentAuthorizationResult result;
        using (context.Begin(new SquarePaymentAttemptContext(attemptGuid, "bound-checkout-idempotency")))
        {
            result = await client.AuthorizeAsync(5m, CreateSession());
        }

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, repository.MarkFailedCount);
        Assert.Equal(attemptGuid, repository.FailedAttemptGuid);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, repository.FailedStatus);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_unknown_when_payment_body_id_does_not_match_checkout_payment_id()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-other",
                "status": "COMPLETED",
                "amount_money": { "amount": 500, "currency": "AUD" }
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square payment response does not match the queried payment.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_unknown_when_checkout_post_was_sent_but_times_out_before_response()
    {
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            postStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after timeout.");
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings(TimeSpan.FromMilliseconds(50))),
            CreateApiClient(handler));

        var authorizeTask = client.AuthorizeAsync(5m, CreateSession());
        await postStarted.Task;
        var result = await authorizeTask;

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task RecoverSquareAsync_marks_attempt_unknown_when_payment_body_id_does_not_match_query()
    {
        var attempt = CreateSquareRecoveryAttempt("checkout-expected");
        var repository = new RecordingFailureSquarePaymentAttemptRepository();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/payments/", StringComparison.Ordinal))
            {
                return JsonResponse("{ \"payment\": { \"id\": \"payment-other\", \"status\": \"COMPLETED\", \"amount_money\": { \"amount\": 500, \"currency\": \"AUD\" } } }");
            }

            return JsonResponse("{ \"checkout\": { \"id\": \"checkout-expected\", \"status\": \"COMPLETED\", \"amount_money\": { \"amount\": 500, \"currency\": \"AUD\" }, \"payment_ids\": [ \"payment-expected\" ] } }");
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squarePaymentAttemptRepository: repository);

        var result = await ((IInstallmentTerminalRecoveryClient)client).RecoverSquareAsync(attempt, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(1, repository.MarkFailedCount);
        Assert.Equal(attempt.AttemptGuid, repository.FailedAttemptGuid);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Unknown, repository.FailedStatus);
    }

    [Fact]
    public async Task AuthorizeAsync_marks_result_unknown_for_failed_payment_lookup_after_checkout_is_created()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            HttpStatusCode.BadRequest,
            """
            {
              "errors": [
                {
                  "code": "BAD_REQUEST",
                  "detail": "Payment is unavailable."
                }
              ]
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square payment failed with HTTP 400: BAD_REQUEST: Payment is unavailable.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_invalid_response_when_square_payment_payload_is_missing_payment()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "not_payment": {
                "id": "payment-1"
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_invalid_response_when_square_payment_is_missing_amount_money()
    {
        var handler = CreateCompletedCheckoutThenPaymentHandler(
            """
            {
              "payment": {
                "id": "payment-1",
                "status": "COMPLETED"
              }
            }
            """);
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(CreateSquareSettings()), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_does_not_refresh_square_token_when_payment_lookup_is_unauthorized()
    {
        var requests = new List<HttpRequestMessage>();
        var paymentLookupCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-payment-refresh",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                paymentLookupCount++;
                if (paymentLookupCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        ReasonPhrase = "Unauthorized"
                    };
                }

                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-1",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-payment-refresh",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-1" ]
                  }
                }
                """);
        });
        var tokenProvider = new FakeSquareAccessTokenProvider(RefreshedToken);
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squareAccessTokenProvider: tokenProvider);

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal(0, tokenProvider.ForceRefreshCount);
        Assert.Collection(
            requests,
            create =>
            {
                AssertHbposApiRequest(create, "api/v1/square/checkouts");
                AssertNoSquareHeaders(create);
            },
            status =>
            {
                AssertHbposApiRequest(status, "api/v1/square/checkouts/checkout-payment-refresh?environment=Production");
                AssertNoSquareHeaders(status);
            },
            failedPayment =>
            {
                AssertHbposApiRequest(failedPayment, "api/v1/square/payments/payment-1?environment=Production");
                AssertNoSquareHeaders(failedPayment);
            });
    }

    [Fact]
    public async Task AuthorizeAsync_normalizes_devices_api_id_for_square_checkout()
    {
        using var logs = new ConsoleLogCapture();
        HttpRequestMessage? createRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                createRequest = CloneRequestWithBody(request);
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-normalized",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-normalized",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-normalized",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-normalized" ]
                  }
                }
                """);
        });
        var settings = CreateSquareSettings() with { SquareDeviceId = "device:533CS145C3000413" };
        var client = new ConfiguredCardTerminalClient(new StaticCardTerminalSettingsProvider(settings), CreateApiClient(handler));

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.True(result.Approved);
        Assert.NotNull(createRequest);
        Assert.Equal("533CS145C3000413", ReadCheckoutDeviceId(createRequest!));
        AssertContainsLogLine(logs.Lines, "storedSquareDeviceId=device:533CS145C3000413 checkoutDeviceId=533CS145C3000413");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_does_not_refresh_square_token_when_checkout_is_unauthorized()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (requests.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    ReasonPhrase = "Unauthorized"
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-1",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "payment": {
                        "id": "payment-1",
                        "status": "COMPLETED",
                        "amount_money": { "amount": 500, "currency": "AUD" }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-1",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-1" ]
                  }
                }
                """);
        });
        var settings = CreateSquareSettings();
        var tokenProvider = new FakeSquareAccessTokenProvider(RefreshedToken);
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            CreateApiClient(handler),
            squareAccessTokenProvider: tokenProvider);

        var result = await client.AuthorizeAsync(5m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal(0, tokenProvider.ForceRefreshCount);
        var request = Assert.Single(requests);
        AssertHbposApiRequest(request, "api/v1/square/checkouts");
        AssertNoSquareHeaders(request);
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_failure_when_square_checkout_is_canceled_after_cancel_requested()
    {
        var requests = new List<HttpRequestMessage>();
        var statusPollCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-2",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            statusPollCount++;
            return JsonResponse(
                statusPollCount == 1
                    ? """
                      {
                        "checkout": {
                          "id": "checkout-2",
                          "status": "CANCEL_REQUESTED"
                        }
                      }
                      """
                    : """
                      {
                        "checkout": {
                          "id": "checkout-2",
                          "status": "CANCELED"
                        }
                      }
                      """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareCanceledSeller", result.StatusKey);
        Assert.Equal("Square checkout was canceled. Please start the card payment again.", result.Message);
        Assert.Equal(3, requests.Count);
    }

    [Theory]
    [InlineData("BUYER_CANCELED", "payment.card.squareCanceledBuyer", "Square checkout was canceled by the buyer. Ask the customer to try again or choose another payment method.")]
    [InlineData("SELLER_CANCELED", "payment.card.squareCanceledSeller", "Square checkout was canceled. Please start the card payment again.")]
    [InlineData("CANCEL_REQUESTED", "payment.card.squareCanceledSeller", "Square checkout was canceled. Please start the card payment again.")]
    [InlineData("SOMETHING_ELSE", "payment.card.squareCanceled", "Square checkout was not completed. Please try again.")]
    public async Task AuthorizeAsync_maps_square_cancel_reason_to_friendly_status(
        string cancelReason,
        string expectedStatusKey,
        string expectedMessage)
    {
        var statusPollCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel-reason",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            statusPollCount++;
            return JsonResponse(
                statusPollCount == 1
                    ? """
                      {
                        "checkout": {
                          "id": "checkout-cancel-reason",
                          "status": "IN_PROGRESS"
                        }
                      }
                      """
                    : $$"""
                      {
                        "checkout": {
                          "id": "checkout-cancel-reason",
                          "status": "CANCELED",
                          "cancel_reason": "{{cancelReason}}"
                        }
                      }
                      """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal(expectedStatusKey, result.StatusKey);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_maps_in_progress_square_timeout_to_customer_timeout_status()
    {
        var statusPollCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout-customer",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            statusPollCount++;
            return JsonResponse(
                statusPollCount == 1
                    ? """
                      {
                        "checkout": {
                          "id": "checkout-timeout-customer",
                          "status": "IN_PROGRESS"
                        }
                      }
                      """
                    : """
                      {
                        "checkout": {
                          "id": "checkout-timeout-customer",
                          "status": "CANCELED",
                          "cancel_reason": "TIMED_OUT"
                        }
                      }
                      """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareTimedOut", result.StatusKey);
        Assert.Equal("Square checkout timed out before the customer completed payment.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_maps_pending_square_timed_out_checkout_to_terminal_not_picked_up_status()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout-terminal",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-timeout-terminal",
                    "status": "CANCELED",
                    "cancel_reason": "TIMED_OUT"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareTerminalNotPickedUp", result.StatusKey);
        Assert.Equal("Square terminal did not pick up the checkout. Check that the terminal is online, then try again.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_maps_sandbox_square_timeout_device_to_timeout_status()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    $$"""
                    {
                      "checkout": {
                        "id": "checkout-sandbox-timeout",
                        "status": "PENDING",
                        "device_options": {
                          "device_id": "{{SquareSandboxTerminalDeviceIds.SquareTimedOut}}"
                        }
                      }
                    }
                    """);
            }

            return JsonResponse(
                $$"""
                {
                  "checkout": {
                    "id": "checkout-sandbox-timeout",
                    "status": "CANCELED",
                    "cancel_reason": "TIMED_OUT",
                    "device_options": {
                      "device_id": "{{SquareSandboxTerminalDeviceIds.SquareTimedOut}}"
                    }
                  }
                }
                """);
        });
        var settings = CreateSquareSettings() with
        {
            Environment = CardTerminalEnvironment.Sandbox,
            SquareDeviceId = SquareSandboxTerminalDeviceIds.SquareTimedOut,
            SquareApiBaseUrl = CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Sandbox)
        };
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareTimedOut", result.StatusKey);
        Assert.Equal("Square checkout timed out before the customer completed payment.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_marks_square_canceled_attempt_with_single_failure_update()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-buyer-canceled",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-buyer-canceled",
                    "status": "CANCELED",
                    "cancel_reason": "BUYER_CANCELED"
                  }
                }
                """);
        });
        var contextAccessor = new SquarePaymentAttemptContextAccessor();
        var repository = new RecordingFailureSquarePaymentAttemptRepository();
        var attemptGuid = Guid.Parse("99999999-aaaa-bbbb-cccc-999999999999");
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squarePaymentAttemptContextAccessor: contextAccessor,
            squarePaymentAttemptRepository: repository);

        PaymentAuthorizationResult result;
        using (contextAccessor.Begin(new SquarePaymentAttemptContext(attemptGuid, "idem-buyer-cancel")))
        {
            result = await client.AuthorizeAsync(10m, CreateSession());
        }

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareCanceledBuyer", result.StatusKey);
        Assert.Equal(0, repository.UpdateCheckoutStatusCount);
        Assert.Equal(1, repository.MarkFailedCount);
        Assert.Equal(attemptGuid, repository.FailedAttemptGuid);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Canceled, repository.FailedStatus);
        Assert.Equal("CANCELED", repository.FailedCheckoutStatus);
        Assert.Equal("BUYER_CANCELED", repository.FailedCancelReason);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_square_error_detail_for_failed_checkout_request()
    {
        using var logs = new ConsoleLogCapture();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return JsonResponse(
                HttpStatusCode.BadRequest,
                """
                {
                  "errors": [
                    {
                      "code": "BAD_REQUEST",
                      "detail": "Device is offline."
                    }
                  ]
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareTerminalOffline", result.StatusKey);
        Assert.Equal("Square terminal is offline. Check the terminal network and try again.", result.Message);
        AssertContainsLogLine(logs.Lines, "checkout create failed http=400 detail=BAD_REQUEST: Device is offline.");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_failure_for_unexpected_square_status()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-3",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-3",
                    "status": "BROKEN"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square checkout entered unexpected status 'BROKEN'.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_invalid_response_when_square_payload_is_missing_checkout()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return JsonResponse(
                """
                {
                  "not_checkout": {
                    "id": "checkout-4"
                  }
                }
                """);
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));

        var result = await client.AuthorizeAsync(10m, CreateSession());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal("Square terminal returned an invalid response.", result.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_cleans_up_square_checkout_on_manual_cancel()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            lock (requests)
            {
                requests.Add(CloneRequest(request));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel",
                        "status": "CANCEL_REQUESTED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/dismiss", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel",
                        "status": "CANCELED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-cancel",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            getStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler));
        using var cancellationTokenSource = new CancellationTokenSource();
        var authorizeTask = client.AuthorizeAsync(10m, CreateSession(), cancellationTokenSource.Token);

        await getStarted.Task;
        cancellationTokenSource.Cancel();

        var result = await authorizeTask;
        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Collection(
            requests,
            create =>
            {
                AssertHbposApiRequest(create, "api/v1/square/checkouts");
                AssertNoSquareHeaders(create);
            },
            status =>
            {
                AssertHbposApiRequest(status, "api/v1/square/checkouts/checkout-cancel?environment=Production");
                AssertNoSquareHeaders(status);
            },
            cancel =>
            {
                AssertHbposApiRequest(cancel, "api/v1/square/checkouts/checkout-cancel/cancel");
                AssertNoSquareHeaders(cancel);
            },
            dismiss =>
            {
                AssertHbposApiRequest(dismiss, "api/v1/square/checkouts/checkout-cancel/dismiss");
                AssertNoSquareHeaders(dismiss);
            });
        AssertContainsLogLine(logs.Lines, "authorize canceled checkoutId=checkout-cancel reason=caller-cancelled; starting cleanup");
        AssertContainsLogLine(logs.Lines, "cleanup start checkoutId=checkout-cancel allowRefresh=True");
        AssertContainsLogLine(logs.Lines, "checkout cancel result checkoutId=checkout-cancel status=CANCEL_REQUESTED shouldDismiss=True");
        AssertContainsLogLine(logs.Lines, "cleanup dismiss required checkoutId=checkout-cancel");
        AssertContainsLogLine(logs.Lines, "checkout dismiss result checkoutId=checkout-cancel http=200");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    [Fact]
    public async Task AuthorizeAsync_cleans_up_square_checkout_on_timeout()
    {
        var requests = new List<HttpRequestMessage>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            lock (requests)
            {
                requests.Add(CloneRequest(request));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout",
                        "status": "CANCEL_REQUESTED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/dismiss", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout",
                        "status": "CANCELED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-timeout",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            getStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after timeout.");
        });
        var settings = CreateSquareSettings(terminalTimeout: TimeSpan.FromMilliseconds(50));
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(settings),
            CreateApiClient(handler));

        var authorizeTask = client.AuthorizeAsync(10m, CreateSession());
        await getStarted.Task;

        var result = await authorizeTask;

        Assert.False(result.Approved);
        Assert.Equal("payment.card.squareTerminalNotPickedUp", result.StatusKey);
        Assert.Equal("Square terminal did not pick up the checkout. Check that the terminal is online, then try again.", result.Message);
        Assert.Collection(
            requests,
            create =>
            {
                AssertHbposApiRequest(create, "api/v1/square/checkouts");
                AssertNoSquareHeaders(create);
            },
            status =>
            {
                AssertHbposApiRequest(status, "api/v1/square/checkouts/checkout-timeout?environment=Production");
                AssertNoSquareHeaders(status);
            },
            cancel =>
            {
                AssertHbposApiRequest(cancel, "api/v1/square/checkouts/checkout-timeout/cancel");
                AssertNoSquareHeaders(cancel);
            },
            dismiss =>
            {
                AssertHbposApiRequest(dismiss, "api/v1/square/checkouts/checkout-timeout/dismiss");
                AssertNoSquareHeaders(dismiss);
            });
    }

    [Fact]
    public async Task AuthorizeAsync_does_not_refresh_square_token_when_cancel_cleanup_is_unauthorized()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            lock (requests)
            {
                requests.Add(CloneRequest(request));
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/dismiss", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-refresh-cancel",
                        "status": "CANCELED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
            {
                if (requests.Count == 3)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-refresh-cancel",
                        "status": "CANCEL_REQUESTED"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-refresh-cancel",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            getStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        var tokenProvider = new FakeSquareAccessTokenProvider(RefreshedToken);
        var client = new ConfiguredCardTerminalClient(
            new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            CreateApiClient(handler),
            squareAccessTokenProvider: tokenProvider);
        using var cancellationTokenSource = new CancellationTokenSource();
        var authorizeTask = client.AuthorizeAsync(10m, CreateSession(), cancellationTokenSource.Token);

        await getStarted.Task;
        cancellationTokenSource.Cancel();

        var result = await authorizeTask;
        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(0, tokenProvider.ForceRefreshCount);
        Assert.Collection(
            requests,
            create =>
            {
                AssertHbposApiRequest(create, "api/v1/square/checkouts");
                AssertNoSquareHeaders(create);
            },
            status =>
            {
                AssertHbposApiRequest(status, "api/v1/square/checkouts/checkout-refresh-cancel?environment=Production");
                AssertNoSquareHeaders(status);
            },
            cancel =>
            {
                AssertHbposApiRequest(cancel, "api/v1/square/checkouts/checkout-refresh-cancel/cancel");
                AssertNoSquareHeaders(cancel);
            },
            dismiss =>
            {
                AssertHbposApiRequest(dismiss, "api/v1/square/checkouts/checkout-refresh-cancel/dismiss");
                AssertNoSquareHeaders(dismiss);
            });
        AssertContainsLogLine(logs.Lines, "checkout cancel failed checkoutId=checkout-refresh-cancel http=401");
        AssertContainsLogLine(logs.Lines, "checkout dismiss result checkoutId=checkout-refresh-cancel http=200");
        AssertNoSensitiveTokenLogged(logs.Lines);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "1001",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSquareSettings(TimeSpan? terminalTimeout = null)
    {
        return new CardTerminalSettings(
            CardProcessorKind.Square,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            InitialToken,
            "LOC-1",
            "DEV-1",
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            terminalTimeout ?? TimeSpan.FromSeconds(10));
    }

    private static CardTerminalSettings CreateLinklySettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Linkly,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(10),
            LinklyConnectionMode.LocalIp);
    }

    private static LocalCardPaymentAttempt CreateLinklyRecoveryAttempt(
        string txnRef,
        decimal amount = 10m,
        string txnType = "P")
    {
        var now = DateTimeOffset.Parse("2026-08-23T10:00:00+10:00");
        return new LocalCardPaymentAttempt(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            null,
            txnRef,
            CardProcessorKind.Linkly.ToString(),
            CardTerminalEnvironment.Production.ToString(),
            LinklyConnectionMode.LocalIp.ToString(),
            txnType,
            amount,
            LocalCardPaymentAttemptStatus.SessionStarted,
            "{}",
            "1001",
            "TERM-1",
            "C001",
            null,
            null,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            null,
            null);
    }

    private static LocalSquarePaymentAttempt CreateSquareRecoveryAttempt(string checkoutId)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalSquarePaymentAttempt(
            Guid.NewGuid(), checkoutId, "square-recovery-idempotency", "DEV-1", "LOC-1", "Production", 5m, 500, "AUD",
            LocalSquarePaymentAttemptStatus.Unknown, "COMPLETED", null, "{}", "1001", "TERM-1", "C001",
            null, null, null, null, now, now, null, null, null);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return JsonResponse(HttpStatusCode.OK, json);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static StubHttpMessageHandler CreateCompletedCheckoutThenPaymentHandler(string paymentJson)
    {
        return CreateCompletedCheckoutThenPaymentHandler(HttpStatusCode.OK, paymentJson);
    }

    private static StubHttpMessageHandler CreateCompletedCheckoutThenPaymentHandler(HttpStatusCode paymentStatusCode, string paymentJson)
    {
        return new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(
                    """
                    {
                      "checkout": {
                        "id": "checkout-verified",
                        "status": "PENDING"
                      }
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.Contains("/api/v1/square/payments/", StringComparison.Ordinal))
            {
                return JsonResponse(paymentStatusCode, paymentJson);
            }

            return JsonResponse(
                """
                {
                  "checkout": {
                    "id": "checkout-verified",
                    "status": "COMPLETED",
                    "amount_money": { "amount": 500, "currency": "AUD" },
                    "payment_ids": [ "payment-1" ]
                  }
                }
                """);
        });
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static HttpRequestMessage CloneRequestWithBody(HttpRequestMessage request)
    {
        var clone = CloneRequest(request);
        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return clone;
    }

    private static string? ReadCheckoutDeviceId(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("deviceId")
            .GetString();
    }

    private static HttpClient CreateApiClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = HbposApiBaseAddress
        };
    }

    private static void AssertHbposApiRequest(HttpRequestMessage request, string relativePathAndQuery)
    {
        Assert.Equal(new Uri(HbposApiBaseAddress, relativePathAndQuery), request.RequestUri);
        var absoluteUri = request.RequestUri!.AbsoluteUri;
        Assert.DoesNotContain("connect.squareup.com", absoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connect.squareupsandbox.com", absoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoSquareHeaders(HttpRequestMessage request)
    {
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Square-Version"));
    }

    private static string ReadJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"Missing JSON property '{propertyName}'.");
    }

    private static void AssertContainsLogLine(IEnumerable<string> lines, string expectedMessageFragment)
    {
        Assert.Contains(
            lines,
            line => line.Contains("[HBPOS][Client][Square]", StringComparison.Ordinal) &&
                line.Contains(expectedMessageFragment, StringComparison.Ordinal));
    }

    private static void AssertNoSensitiveTokenLogged(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Assert.DoesNotContain(InitialToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshedToken, line, StringComparison.Ordinal);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }

    private sealed class StubLinklyTerminalClient(PaymentAuthorizationResult result) : ILinklyTerminalClient
    {
        public TaskCompletionSource<PaymentAuthorizationResult>? PurchaseCompletion { get; init; }

        public TaskCompletionSource<bool> PurchaseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RecoverCallCount { get; private set; }

        public string? LastRecoveryTxnRef { get; private set; }

        public decimal LastAmount { get; private set; }

        public decimal LastRefundAmount { get; private set; }

        public string? LastOriginalReference { get; private set; }

        public string? LastPurchaseReference { get; private set; }

        public string? LastRefundTransactionReference { get; private set; }

        public CardTerminalSettings? LastSettings { get; private set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true));
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastAmount = amount;
            LastSettings = settings;
            PurchaseStarted.TrySetResult(true);
            return PurchaseCompletion?.Task ?? Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string txnRef,
            CancellationToken cancellationToken = default)
        {
            LastAmount = amount;
            LastPurchaseReference = txnRef;
            LastSettings = settings;
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string txnRef,
            CancellationToken cancellationToken = default)
        {
            RecoverCallCount++;
            LastRecoveryTxnRef = txnRef;
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            LastRefundAmount = amount;
            LastOriginalReference = originalReference;
            LastSettings = settings;
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> RefundWithReferenceAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            string refundTxnRef,
            CancellationToken cancellationToken = default)
        {
            LastRefundAmount = amount;
            LastOriginalReference = originalReference;
            LastRefundTransactionReference = refundTxnRef;
            LastSettings = settings;
            return Task.FromResult(result);
        }

        public Task<PaymentAuthorizationResult> VoidAsync(
            decimal amount,
            PosSessionState session,
            CardTerminalSettings settings,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingFailureSquarePaymentAttemptRepository : ILocalSquarePaymentAttemptRepository
    {
        public int UpdateCheckoutStatusCount { get; private set; }

        public int MarkFailedCount { get; private set; }

        public Guid? FailedAttemptGuid { get; private set; }

        public LocalSquarePaymentAttemptStatus? FailedStatus { get; private set; }

        public string? FailedCheckoutStatus { get; private set; }

        public string? FailedCancelReason { get; private set; }

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
            return Task.FromResult(true);
        }

        public Task MarkCheckoutCreatedAsync(
            Guid attemptGuid,
            string checkoutId,
            string? checkoutStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateCheckoutStatusAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? cancelReason,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            UpdateCheckoutStatusCount++;
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
            return Task.CompletedTask;
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
            MarkFailedCount++;
            FailedAttemptGuid = attemptGuid;
            FailedStatus = status;
            FailedCheckoutStatus = checkoutStatus;
            FailedCancelReason = cancelReason;
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string? cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(null);
        }

        public Task<IReadOnlyList<LocalSquarePaymentAttempt>> GetOpenRefundAttemptsAsync(
            string storeCode,
            string deviceCode,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalSquarePaymentAttempt>>([]);
        }

        public Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(null);
        }
    }

    private sealed class FakeSquareAccessTokenProvider(string token) : ISquareAccessTokenProvider
    {
        public int ForceRefreshCount { get; private set; }

        public Task<string?> GetSquareAccessTokenAsync(
            CardTerminalEnvironment environment,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForceRefreshCount++;
            }

            return Task.FromResult<string?>(token);
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
