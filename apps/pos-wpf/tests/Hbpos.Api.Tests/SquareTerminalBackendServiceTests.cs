using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Square;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class SquareTerminalBackendServiceTests
{
    [Fact]
    public async Task CreateCheckoutAsync_UsesActiveTokenAndReturnsCheckout()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Production",
            "token-001",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            CreateCheckoutAsyncHandler = (environment, accessToken, request, _) =>
            {
                Assert.Equal("Production", environment);
                Assert.Equal("token-001", accessToken);
                Assert.Equal("device-001", request.DeviceId);
                return Task.FromResult(new SquareTerminalCheckoutRecord(
                    "checkout-001",
                    environment,
                    Status: "PENDING",
                    DeviceId: request.DeviceId,
                    LocationId: request.LocationId,
                    AmountMoney: request.AmountMoney,
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);
        var request = new SquareCreateCheckoutRequest(
            "production",
            "idem-001",
            "device-001",
            "location-001",
            new SquareMoneyDto(1299, "AUD"));

        var response = await service.CreateCheckoutAsync(request, CancellationToken.None);

        Assert.Equal("Production", tokenService.RequestedEnvironments.Single());
        Assert.Equal("checkout-001", response.CheckoutId);
        Assert.Equal("Production", response.Environment);
        Assert.Equal("PENDING", response.Status);
        Assert.Equal("device-001", response.DeviceId);
        Assert.Equal(1299, response.AmountMoney?.Amount);
    }

    [Fact]
    public async Task CreateCheckoutAsync_WhenSandboxUsesConfiguredDevice_ReplacesWithOfficialCheckoutDeviceId()
    {
        const string squareSandboxSuccessDeviceId = "9fa747a2-25ff-48ee-b078-04381f7c828f";
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-sandbox",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            CreateCheckoutAsyncHandler = (environment, accessToken, request, _) =>
            {
                Assert.Equal("Sandbox", environment);
                Assert.Equal("token-sandbox", accessToken);
                Assert.Equal(squareSandboxSuccessDeviceId, request.DeviceId);
                return Task.FromResult(new SquareTerminalCheckoutRecord(
                    "checkout-sandbox",
                    environment,
                    Status: "PENDING",
                    DeviceId: request.DeviceId,
                    LocationId: request.LocationId,
                    AmountMoney: request.AmountMoney,
                    UpdatedAt: new DateTimeOffset(2026, 6, 23, 1, 2, 3, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);
        var request = new SquareCreateCheckoutRequest(
            "sandbox",
            "idem-sandbox",
            "device:paired-terminal",
            "location-sandbox",
            new SquareMoneyDto(1299, "AUD"));

        var response = await service.CreateCheckoutAsync(request, CancellationToken.None);

        Assert.Equal("Sandbox", tokenService.RequestedEnvironments.Single());
        Assert.Equal(squareSandboxSuccessDeviceId, response.DeviceId);
    }

    [Fact]
    public async Task CreateCheckoutAsync_WhenSandboxUsesOfficialCheckoutDeviceId_KeepsSelectedSimulation()
    {
        const string squareSandboxCancelDeviceId = "841100b9-ee60-4537-9bcf-e30b2ba5e215";
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-sandbox",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            CreateCheckoutAsyncHandler = (environment, _, request, _) =>
            {
                Assert.Equal("Sandbox", environment);
                Assert.Equal(squareSandboxCancelDeviceId, request.DeviceId);
                return Task.FromResult(new SquareTerminalCheckoutRecord(
                    "checkout-cancel",
                    environment,
                    Status: "PENDING",
                    DeviceId: request.DeviceId,
                    LocationId: request.LocationId,
                    AmountMoney: request.AmountMoney,
                    UpdatedAt: new DateTimeOffset(2026, 6, 23, 1, 2, 3, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);
        var request = new SquareCreateCheckoutRequest(
            "sandbox",
            "idem-sandbox-cancel",
            $"device:{squareSandboxCancelDeviceId.ToUpperInvariant()}",
            "location-sandbox",
            new SquareMoneyDto(1299, "AUD"));

        var response = await service.CreateCheckoutAsync(request, CancellationToken.None);

        Assert.Equal(squareSandboxCancelDeviceId, response.DeviceId);
    }

    [Fact]
    public async Task CreateCheckoutAsync_WhenSandboxDeviceIdDiffersOnlyByCase_ReusesOriginalRequest()
    {
        const string squareSandboxCancelDeviceId = "841100B9-EE60-4537-9BCF-E30B2BA5E215";
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-sandbox",
            DateTimeOffset.UtcNow));
        SquareCreateCheckoutRequest? forwardedRequest = null;
        var restClient = new FakeSquareTerminalRestClient
        {
            CreateCheckoutAsyncHandler = (environment, _, request, _) =>
            {
                forwardedRequest = request;
                return Task.FromResult(new SquareTerminalCheckoutRecord(
                    "checkout-cancel",
                    environment,
                    Status: "PENDING",
                    DeviceId: request.DeviceId,
                    LocationId: request.LocationId,
                    AmountMoney: request.AmountMoney,
                    UpdatedAt: new DateTimeOffset(2026, 6, 23, 1, 2, 3, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);
        var request = new SquareCreateCheckoutRequest(
            "sandbox",
            "idem-sandbox-cancel",
            squareSandboxCancelDeviceId,
            "location-sandbox",
            new SquareMoneyDto(1299, "AUD"));

        var response = await service.CreateCheckoutAsync(request, CancellationToken.None);

        Assert.Same(request, forwardedRequest);
        Assert.Equal(squareSandboxCancelDeviceId, response.DeviceId);
    }

    [Fact]
    public async Task GetCheckoutAsync_WhenCompleted_LoadsPaymentDetails()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-002",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            GetCheckoutAsyncHandler = (environment, accessToken, checkoutId, _) =>
            {
                Assert.Equal("Sandbox", environment);
                Assert.Equal("token-002", accessToken);
                return Task.FromResult<SquareTerminalCheckoutRecord?>(new SquareTerminalCheckoutRecord(
                    checkoutId,
                    environment,
                    Status: "COMPLETED",
                    DeviceId: "device-001",
                    LocationId: "location-001",
                    AmountMoney: new SquareMoneyDto(1299, "AUD"),
                    PaymentIds: ["payment-001"],
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero)));
            },
            GetPaymentAsyncHandler = (environment, accessToken, paymentId, _) =>
            {
                Assert.Equal("Sandbox", environment);
                Assert.Equal("token-002", accessToken);
                return Task.FromResult<SquarePaymentStatusDto?>(new SquarePaymentStatusDto(
                    paymentId,
                    Status: "COMPLETED",
                    ApprovedMoney: new SquareMoneyDto(1299, "AUD"),
                    TotalMoney: new SquareMoneyDto(1299, "AUD"),
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 1, 4, 5, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);

        var response = await service.GetCheckoutAsync("sandbox", "checkout-001", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("checkout-001", response!.CheckoutId);
        Assert.Equal("COMPLETED", response.Status);
        Assert.NotNull(response.Payment);
        Assert.Equal("payment-001", response.Payment!.PaymentId);
        Assert.Equal(1299, response.Payment.ApprovedMoney?.Amount);
    }

    [Fact]
    public async Task GetCheckoutAsync_WhenLiveCheckoutFails_UsesWebhookCanceledSession()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        repository.Sessions["Sandbox::checkout-canceled"] = new SquareCheckoutSessionRecord
        {
            Environment = "Sandbox",
            CheckoutId = "checkout-canceled",
            Status = "CANCELED",
            Amount = 1299,
            Currency = "AUD",
            PaymentIdsJson = "[]",
            RawCheckoutJson = """
                {
                  "id": "checkout-canceled",
                  "status": "CANCELED",
                  "cancel_reason": "SELLER_CANCELED",
                  "amount_money": { "amount": 1299, "currency": "AUD" },
                  "device_options": { "device_id": "device-fallback" },
                  "location_id": "location-fallback",
                  "payment_ids": []
                }
                """,
            UpdatedAt = new DateTimeOffset(2026, 6, 22, 2, 3, 4, TimeSpan.Zero)
        };
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-fallback",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            GetCheckoutAsyncHandler = (_, _, _, _) =>
                Task.FromException<SquareTerminalCheckoutRecord?>(
                    new SquareTerminalRestException(HttpStatusCode.BadGateway, "Square checkout lookup failed."))
        };
        var service = new SquareTerminalBackendService(
            tokenService,
            restClient,
            checkoutSessionRepository: repository);

        var response = await service.GetCheckoutAsync("sandbox", "checkout-canceled", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("checkout-canceled", response!.CheckoutId);
        Assert.Equal("CANCELED", response.Status);
        Assert.Equal("SELLER_CANCELED", response.CancelReason);
        Assert.Equal("device-fallback", response.DeviceId);
        Assert.Equal("location-fallback", response.LocationId);
        Assert.Equal(1299, response.AmountMoney?.Amount);
        Assert.Empty(response.PaymentIds ?? []);
        Assert.Null(response.Payment);
    }

    [Fact]
    public async Task GetCheckoutAsync_WhenLiveCheckoutReturnsInvalidJson_UsesWebhookSession()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        repository.Sessions["Sandbox::checkout-invalid-json"] = new SquareCheckoutSessionRecord
        {
            Environment = "Sandbox",
            CheckoutId = "checkout-invalid-json",
            Status = "CANCELED",
            Amount = 1999,
            Currency = "AUD",
            RawCheckoutJson = """
                {
                  "id": "checkout-invalid-json",
                  "status": "CANCELED",
                  "cancel_reason": "TIMED_OUT",
                  "amount_money": { "amount": 1999, "currency": "AUD" },
                  "device_options": { "device_id": "device-invalid-json" },
                  "location_id": "location-invalid-json",
                  "payment_ids": []
                }
                """,
            UpdatedAt = new DateTimeOffset(2026, 6, 22, 2, 6, 7, TimeSpan.Zero)
        };
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-invalid-json",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            GetCheckoutAsyncHandler = (_, _, _, _) =>
                Task.FromException<SquareTerminalCheckoutRecord?>(new JsonException("Invalid checkout JSON."))
        };
        var service = new SquareTerminalBackendService(
            tokenService,
            restClient,
            checkoutSessionRepository: repository);

        var response = await service.GetCheckoutAsync("sandbox", "checkout-invalid-json", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("CANCELED", response!.Status);
        Assert.Equal("TIMED_OUT", response.CancelReason);
        Assert.Equal("device-invalid-json", response.DeviceId);
        Assert.Equal("location-invalid-json", response.LocationId);
        Assert.Null(response.Payment);
    }

    [Fact]
    public async Task GetCheckoutAsync_WhenWebhookCompletedSessionHasPaymentId_LoadsPaymentDetails()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        repository.Sessions["Production::checkout-completed"] = new SquareCheckoutSessionRecord
        {
            Environment = "Production",
            CheckoutId = "checkout-completed",
            Status = "COMPLETED",
            Amount = 2599,
            Currency = "AUD",
            PaymentIdsJson = "[\"payment-fallback\"]",
            RawCheckoutJson = """
                {
                  "id": "checkout-completed",
                  "status": "COMPLETED",
                  "amount_money": { "amount": 2599, "currency": "AUD" },
                  "device_options": { "device_id": "device-completed" },
                  "location_id": "location-completed",
                  "payment_ids": [ "payment-fallback" ]
                }
                """,
            UpdatedAt = new DateTimeOffset(2026, 6, 22, 3, 4, 5, TimeSpan.Zero)
        };
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Production",
            "token-completed",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            GetCheckoutAsyncHandler = (_, _, _, _) =>
                Task.FromException<SquareTerminalCheckoutRecord?>(
                    new SquareTerminalRestException(HttpStatusCode.BadGateway, "Square checkout lookup failed.")),
            GetPaymentAsyncHandler = (environment, accessToken, paymentId, _) =>
            {
                Assert.Equal("Production", environment);
                Assert.Equal("token-completed", accessToken);
                Assert.Equal("payment-fallback", paymentId);
                return Task.FromResult<SquarePaymentStatusDto?>(new SquarePaymentStatusDto(
                    paymentId,
                    Status: "COMPLETED",
                    ApprovedMoney: new SquareMoneyDto(2599, "AUD"),
                    TotalMoney: new SquareMoneyDto(2599, "AUD"),
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 3, 5, 6, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(
            tokenService,
            restClient,
            checkoutSessionRepository: repository);

        var response = await service.GetCheckoutAsync("production", "checkout-completed", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("COMPLETED", response!.Status);
        Assert.Equal(["payment-fallback"], response.PaymentIds);
        Assert.NotNull(response.Payment);
        Assert.Equal("payment-fallback", response.Payment!.PaymentId);
        Assert.Equal(2599, response.Payment.ApprovedMoney?.Amount);
    }

    [Fact]
    public async Task GetCheckoutAsync_WhenLiveCheckoutAndWebhookSessionAreMissing_ReturnsNull()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-missing",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            GetCheckoutAsyncHandler = (_, _, _, _) => Task.FromResult<SquareTerminalCheckoutRecord?>(null)
        };
        var service = new SquareTerminalBackendService(
            tokenService,
            restClient,
            checkoutSessionRepository: new InMemorySquareCheckoutSessionRepository());

        var response = await service.GetCheckoutAsync("sandbox", "checkout-missing", CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task CreateRefundAsync_ReturnsRefundResponse()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Production",
            "token-003",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            CreateRefundAsyncHandler = (environment, accessToken, request, _) =>
            {
                Assert.Equal("Production", environment);
                Assert.Equal("token-003", accessToken);
                return Task.FromResult(new SquareRefundResponse(
                    "refund-001",
                    environment,
                    Status: "PENDING",
                    PaymentId: request.PaymentId,
                    AmountMoney: request.AmountMoney,
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);
        var request = new SquareRefundRequest(
            "Production",
            "idem-refund-001",
            "payment-001",
            new SquareMoneyDto(500, "AUD"),
            Reason: "customer changed mind");

        var response = await service.CreateRefundAsync(request, CancellationToken.None);

        Assert.Equal("refund-001", response.RefundId);
        Assert.Equal("PENDING", response.Status);
        Assert.Equal("payment-001", response.PaymentId);
        Assert.Equal(500, response.AmountMoney?.Amount);
    }

    [Fact]
    public async Task GetRefundAsync_UsesActiveTokenAndReturnsCurrentStatus()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Production",
            "token-refund-status",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            GetRefundAsyncHandler = (environment, accessToken, refundId, _) =>
            {
                Assert.Equal("Production", environment);
                Assert.Equal("token-refund-status", accessToken);
                Assert.Equal("refund-001", refundId);
                return Task.FromResult<SquareRefundResponse?>(new SquareRefundResponse(
                    refundId,
                    environment,
                    Status: "COMPLETED",
                    PaymentId: "payment-001",
                    AmountMoney: new SquareMoneyDto(500, "AUD")));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);

        var response = await service.GetRefundAsync(
            "production",
            "refund-001",
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("COMPLETED", response.Status);
        Assert.Equal("payment-001", response.PaymentId);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenSignatureKeyMissing_ThrowsStableForbiddenException()
    {
        var service = new SquareTerminalBackendService(
            new RecordingSquareTokenService(response: null),
            new FakeSquareTerminalRestClient());

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            service.AcceptWebhookAsync(
                new SquareWebhookRequest(
                    "{\"event_id\":\"event-001\",\"type\":\"terminal.checkout.updated\"}",
                    SignatureHeader: "signature-001",
                    SquareEnvironmentHeader: "Sandbox",
                    NotificationUrl: "https://example.com/api/v1/square/webhooks"),
                CancellationToken.None));

        Assert.Equal("SQUARE_WEBHOOK_SIGNATURE_KEY_NOT_CONFIGURED", exception.Code);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("Square webhook signature key is not configured for this environment.", exception.Message);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenSignatureIsInvalid_ThrowsStableForbiddenException()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        var service = CreateWebhookService(repository, CreateWebhookOptions("sandbox-webhook-key"));

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            service.AcceptWebhookAsync(
                new SquareWebhookRequest(
                    CreateCheckoutUpdatedPayload(),
                    SignatureHeader: "invalid-signature",
                    SquareEnvironmentHeader: "Sandbox",
                    NotificationUrl: "https://example.com/api/v1/square/webhooks"),
                CancellationToken.None));

        Assert.Equal("SQUARE_WEBHOOK_SIGNATURE_INVALID", exception.Code);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("Square webhook signature is invalid.", exception.Message);
        Assert.Empty(repository.WebhookEvents);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenConfiguredNotificationUrlDiffersFromRequestUrl_UsesConfiguredNotificationUrl()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        const string requestUrl = "http://internal-gateway/api/v1/square/webhooks";
        const string publicNotificationUrl = "https://payments.example.com/api/v1/square/webhooks";
        var rawBody = CreateCheckoutUpdatedPayload();
        var service = CreateWebhookService(
            repository,
            CreateWebhookOptions(
                "sandbox-webhook-key",
                sandboxNotificationUrl: publicNotificationUrl));
        var signature = CreateSignature("sandbox-webhook-key", publicNotificationUrl, rawBody);

        var response = await service.AcceptWebhookAsync(
            new SquareWebhookRequest(rawBody, signature, "Sandbox", requestUrl),
            CancellationToken.None);

        Assert.Equal("accepted", response.Status);
        Assert.Single(repository.WebhookEvents);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenEnvironmentHeaderMissing_UsesConfiguredSignatureKeyThatMatches()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        const string notificationUrl = "https://example.com/api/v1/square/webhooks";
        var rawBody = CreateCheckoutUpdatedPayload();
        var service = CreateWebhookService(repository, Options.Create(new SquareWebhookOptions
        {
            WebhookSignatureKeys =
            {
                ["Production"] = "production-webhook-key",
                ["Sandbox"] = "sandbox-webhook-key"
            }
        }));
        var signature = CreateSignature("sandbox-webhook-key", notificationUrl, rawBody);

        var response = await service.AcceptWebhookAsync(
            new SquareWebhookRequest(rawBody, signature, SquareEnvironmentHeader: null, notificationUrl),
            CancellationToken.None);

        Assert.Equal("accepted", response.Status);
        Assert.Single(repository.WebhookEvents);
        var session = await repository.GetCheckoutSessionAsync("Sandbox", "checkout-001", CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal("COMPLETED", session!.Status);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenSignatureValid_PersistsWebhookEventAndCheckoutState()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        const string notificationUrl = "https://example.com/api/v1/square/webhooks";
        const string rawBody = """
            {
              "event_id": "event-001",
              "type": "terminal.checkout.updated",
              "created_at": "2026-06-22T01:02:03Z",
              "data": {
                "object": {
                  "checkout": {
                    "id": "checkout-001",
                    "status": "COMPLETED",
                    "amount_money": {
                      "amount": 1299,
                      "currency": "AUD"
                    },
                    "device_options": {
                      "device_id": "device-001"
                    },
                    "location_id": "location-001",
                    "payment_ids": [
                      "payment-001"
                    ]
                  }
                }
              }
            }
            """;
        var service = CreateWebhookService(repository, CreateWebhookOptions("sandbox-webhook-key"));
        var signature = CreateSignature("sandbox-webhook-key", notificationUrl, rawBody);

        var response = await service.AcceptWebhookAsync(
            new SquareWebhookRequest(rawBody, signature, "Sandbox", notificationUrl),
            CancellationToken.None);

        Assert.Equal("accepted", response.Status);
        Assert.Equal("event-001", response.EventId);
        Assert.Single(repository.WebhookEvents);
        var session = await repository.GetCheckoutSessionAsync("Sandbox", "checkout-001", CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal("COMPLETED", session!.Status);
        Assert.Equal(1299, session.Amount);
        Assert.Equal("AUD", session.Currency);
        Assert.Equal("device-001", session.DeviceId);
        Assert.Equal("location-001", session.LocationId);
        Assert.Equal("payment-001", session.PaymentId);
        Assert.Equal("[\"payment-001\"]", session.PaymentIdsJson);
        Assert.Equal("event-001", session.LastEventId);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenEventIdDuplicated_ReturnsDeduplicatedWithoutReapplyingState()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        const string notificationUrl = "https://example.com/api/v1/square/webhooks";
        var rawBody = CreateCheckoutUpdatedPayload();
        var service = CreateWebhookService(repository, CreateWebhookOptions("sandbox-webhook-key"));
        var signature = CreateSignature("sandbox-webhook-key", notificationUrl, rawBody);
        var request = new SquareWebhookRequest(rawBody, signature, "Sandbox", notificationUrl);

        var first = await service.AcceptWebhookAsync(request, CancellationToken.None);
        var second = await service.AcceptWebhookAsync(request, CancellationToken.None);

        Assert.Equal("accepted", first.Status);
        Assert.Equal("deduplicated", second.Status);
        Assert.Single(repository.WebhookEvents);
        Assert.Equal(1, repository.UpsertCheckoutCallCount);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenOlderNonTerminalEventArrivesAfterCompleted_KeepsCompletedState()
    {
        var repository = new InMemorySquareCheckoutSessionRepository();
        const string notificationUrl = "https://example.com/api/v1/square/webhooks";
        var service = CreateWebhookService(repository, CreateWebhookOptions("sandbox-webhook-key"));

        var completedRawBody = CreateCheckoutUpdatedPayload(
            eventId: "event-001",
            createdAt: "2026-06-22T01:02:03Z",
            status: "COMPLETED");
        var completedSignature = CreateSignature("sandbox-webhook-key", notificationUrl, completedRawBody);
        var completedResponse = await service.AcceptWebhookAsync(
            new SquareWebhookRequest(completedRawBody, completedSignature, "Sandbox", notificationUrl),
            CancellationToken.None);

        var olderPendingRawBody = CreateCheckoutUpdatedPayload(
            eventId: "event-002",
            createdAt: "2026-06-22T01:01:03Z",
            status: "PENDING");
        var olderPendingSignature = CreateSignature("sandbox-webhook-key", notificationUrl, olderPendingRawBody);
        var olderPendingResponse = await service.AcceptWebhookAsync(
            new SquareWebhookRequest(olderPendingRawBody, olderPendingSignature, "Sandbox", notificationUrl),
            CancellationToken.None);

        var session = await repository.GetCheckoutSessionAsync("Sandbox", "checkout-001", CancellationToken.None);

        Assert.Equal("accepted", completedResponse.Status);
        Assert.Equal("accepted", olderPendingResponse.Status);
        Assert.Equal(2, repository.WebhookEvents.Count);
        Assert.NotNull(session);
        Assert.Equal("COMPLETED", session!.Status);
        Assert.Equal("event-001", session.LastEventId);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero), session.UpdatedAt);
    }

    [Fact]
    public async Task CancelCheckoutAsync_UsesActiveTokenAndReturnsCheckoutResponse()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Production",
            "token-004",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            CancelCheckoutAsyncHandler = (environment, accessToken, checkoutId, request, _) =>
            {
                Assert.Equal("Production", environment);
                Assert.Equal("token-004", accessToken);
                Assert.Equal("checkout-001", checkoutId);
                Assert.Equal("customer requested cancel", request.Reason);
                return Task.FromResult(new SquareTerminalCheckoutRecord(
                    checkoutId,
                    environment,
                    Status: "CANCELED",
                    DeviceId: "device-001",
                    LocationId: "location-001",
                    AmountMoney: new SquareMoneyDto(1299, "AUD"),
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);

        var response = await service.CancelCheckoutAsync(
            "checkout-001",
            new SquareCheckoutActionRequest("production", Reason: "customer requested cancel"),
            CancellationToken.None);

        Assert.Equal("Production", tokenService.RequestedEnvironments.Single());
        Assert.Equal("checkout-001", response.CheckoutId);
        Assert.Equal("Production", response.Environment);
        Assert.Equal("CANCELED", response.Status);
        Assert.Equal("device-001", response.DeviceId);
        Assert.Equal(1299, response.AmountMoney?.Amount);
    }

    [Fact]
    public async Task DismissCheckoutAsync_UsesActiveTokenAndReturnsCheckoutResponse()
    {
        var tokenService = new RecordingSquareTokenService(new SquareTokenResponse(
            "Sandbox",
            "token-005",
            DateTimeOffset.UtcNow));
        var restClient = new FakeSquareTerminalRestClient
        {
            DismissCheckoutAsyncHandler = (environment, accessToken, checkoutId, request, _) =>
            {
                Assert.Equal("Sandbox", environment);
                Assert.Equal("token-005", accessToken);
                Assert.Equal("checkout-002", checkoutId);
                Assert.Equal("customer acknowledged", request.Reason);
                return Task.FromResult(new SquareTerminalCheckoutRecord(
                    checkoutId,
                    environment,
                    Status: "COMPLETED",
                    DeviceId: "device-002",
                    LocationId: "location-002",
                    AmountMoney: new SquareMoneyDto(2599, "AUD"),
                    UpdatedAt: new DateTimeOffset(2026, 6, 22, 1, 4, 5, TimeSpan.Zero)));
            }
        };
        var service = new SquareTerminalBackendService(tokenService, restClient);

        var response = await service.DismissCheckoutAsync(
            "checkout-002",
            new SquareCheckoutActionRequest("sandbox", Reason: "customer acknowledged"),
            CancellationToken.None);

        Assert.Equal("Sandbox", tokenService.RequestedEnvironments.Single());
        Assert.Equal("checkout-002", response.CheckoutId);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("COMPLETED", response.Status);
        Assert.Equal("device-002", response.DeviceId);
        Assert.Equal(2599, response.AmountMoney?.Amount);
    }

    [Fact]
    public async Task CreateDeviceCodeAsync_WhenIdempotencyKeyMissing_ThrowsStableException()
    {
        var service = new SquareTerminalBackendService(
            new RecordingSquareTokenService(new SquareTokenResponse(
                "Production",
                "token-006",
                DateTimeOffset.UtcNow)),
            new FakeSquareTerminalRestClient());

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            service.CreateDeviceCodeAsync(
                new SquareCreateDeviceCodeRequest(
                    "Production",
                    IdempotencyKey: "",
                    LocationId: "location-001",
                    Name: "Front Counter"),
                CancellationToken.None));

        Assert.Equal("SQUARE_IDEMPOTENCY_KEY_REQUIRED", exception.Code);
        Assert.Equal("idempotencyKey is required.", exception.Message);
    }

    [Theory]
    [InlineData("checkout")]
    [InlineData("refund")]
    public async Task MutatingPaymentRequests_WhenIdempotencyKeyMissing_ThrowStableException(string operation)
    {
        var service = new SquareTerminalBackendService(
            new RecordingSquareTokenService(new SquareTokenResponse(
                "Production",
                "token-006",
                DateTimeOffset.UtcNow)),
            new FakeSquareTerminalRestClient());

        var exception = operation == "checkout"
            ? await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
                service.CreateCheckoutAsync(
                    new SquareCreateCheckoutRequest(
                        "Production",
                        IdempotencyKey: "",
                        DeviceId: "device-001",
                        LocationId: "location-001",
                        AmountMoney: new SquareMoneyDto(1299, "AUD")),
                    CancellationToken.None))
            : await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
                service.CreateRefundAsync(
                    new SquareRefundRequest(
                        "Production",
                        IdempotencyKey: "",
                        PaymentId: "payment-001",
                        AmountMoney: new SquareMoneyDto(1299, "AUD")),
                    CancellationToken.None));

        Assert.Equal("SQUARE_IDEMPOTENCY_KEY_REQUIRED", exception.Code);
        Assert.Equal("idempotencyKey is required.", exception.Message);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task CreateCheckoutAsync_WhenTokenMissing_ThrowsStableException()
    {
        var service = new SquareTerminalBackendService(
            new RecordingSquareTokenService(response: null),
            new FakeSquareTerminalRestClient());
        var request = new SquareCreateCheckoutRequest(
            "Production",
            "idem-001",
            "device-001",
            "location-001",
            new SquareMoneyDto(1299, "AUD"));

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            service.CreateCheckoutAsync(request, CancellationToken.None));

        Assert.Equal("SQUARE_TOKEN_NOT_CONFIGURED", exception.Code);
        Assert.Equal("Square token is not configured for this environment.", exception.Message);
    }

    private sealed class RecordingSquareTokenService(SquareTokenResponse? response) : ISquareTokenService
    {
        public List<string> RequestedEnvironments { get; } = [];

        public Task<SquareTokenResponse?> GetActiveTokenAsync(string environment, CancellationToken cancellationToken)
        {
            RequestedEnvironments.Add(environment);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeSquareTerminalRestClient : ISquareTerminalRestClient
    {
        public Func<string, string, CancellationToken, Task<IReadOnlyList<SquareLocationDto>>>? GetLocationsAsyncHandler { get; init; }

        public Func<string, string, string, CancellationToken, Task<IReadOnlyList<SquareDeviceDto>>>? GetDevicesAsyncHandler { get; init; }

        public Func<string, string, string, CancellationToken, Task<IReadOnlyList<SquareDeviceCodeDto>>>? GetDeviceCodesAsyncHandler { get; init; }

        public Func<string, string, SquareCreateDeviceCodeRequest, CancellationToken, Task<SquareDeviceCodeDto>>? CreateDeviceCodeAsyncHandler { get; init; }

        public Func<string, string, string, CancellationToken, Task<SquareDeviceCodeDto?>>? GetDeviceCodeAsyncHandler { get; init; }

        public Func<string, string, SquareCreateCheckoutRequest, CancellationToken, Task<SquareTerminalCheckoutRecord>>? CreateCheckoutAsyncHandler { get; init; }

        public Func<string, string, string, CancellationToken, Task<SquareTerminalCheckoutRecord?>>? GetCheckoutAsyncHandler { get; init; }

        public Func<string, string, string, SquareCheckoutActionRequest, CancellationToken, Task<SquareTerminalCheckoutRecord>>? CancelCheckoutAsyncHandler { get; init; }

        public Func<string, string, string, SquareCheckoutActionRequest, CancellationToken, Task<SquareTerminalCheckoutRecord>>? DismissCheckoutAsyncHandler { get; init; }

        public Func<string, string, string, CancellationToken, Task<SquarePaymentStatusDto?>>? GetPaymentAsyncHandler { get; init; }

        public Func<string, string, SquareRefundRequest, CancellationToken, Task<SquareRefundResponse>>? CreateRefundAsyncHandler { get; init; }

        public Func<string, string, string, CancellationToken, Task<SquareRefundResponse?>>? GetRefundAsyncHandler { get; init; }

        public Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(string environment, string accessToken, CancellationToken cancellationToken)
        {
            return GetLocationsAsyncHandler?.Invoke(environment, accessToken, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<SquareLocationDto>>([]);
        }

        public Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(string environment, string accessToken, string locationId, CancellationToken cancellationToken)
        {
            return GetDevicesAsyncHandler?.Invoke(environment, accessToken, locationId, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<SquareDeviceDto>>([]);
        }

        public Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(string environment, string accessToken, string locationId, CancellationToken cancellationToken)
        {
            return GetDeviceCodesAsyncHandler?.Invoke(environment, accessToken, locationId, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<SquareDeviceCodeDto>>([]);
        }

        public Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(string environment, string accessToken, SquareCreateDeviceCodeRequest request, CancellationToken cancellationToken)
        {
            return CreateDeviceCodeAsyncHandler?.Invoke(environment, accessToken, request, cancellationToken)
                ?? Task.FromResult(new SquareDeviceCodeDto("device-code-default"));
        }

        public Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(string environment, string accessToken, string deviceCodeId, CancellationToken cancellationToken)
        {
            return GetDeviceCodeAsyncHandler?.Invoke(environment, accessToken, deviceCodeId, cancellationToken)
                ?? Task.FromResult<SquareDeviceCodeDto?>(null);
        }

        public Task<SquareTerminalCheckoutRecord> CreateCheckoutAsync(string environment, string accessToken, SquareCreateCheckoutRequest request, CancellationToken cancellationToken)
        {
            return CreateCheckoutAsyncHandler?.Invoke(environment, accessToken, request, cancellationToken)
                ?? Task.FromResult(new SquareTerminalCheckoutRecord("checkout-default", environment));
        }

        public Task<SquareTerminalCheckoutRecord?> GetCheckoutAsync(string environment, string accessToken, string checkoutId, CancellationToken cancellationToken)
        {
            return GetCheckoutAsyncHandler?.Invoke(environment, accessToken, checkoutId, cancellationToken)
                ?? Task.FromResult<SquareTerminalCheckoutRecord?>(null);
        }

        public Task<SquareTerminalCheckoutRecord> CancelCheckoutAsync(string environment, string accessToken, string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken)
        {
            return CancelCheckoutAsyncHandler?.Invoke(environment, accessToken, checkoutId, request, cancellationToken)
                ?? Task.FromResult(new SquareTerminalCheckoutRecord(checkoutId, environment));
        }

        public Task<SquareTerminalCheckoutRecord> DismissCheckoutAsync(string environment, string accessToken, string checkoutId, SquareCheckoutActionRequest request, CancellationToken cancellationToken)
        {
            return DismissCheckoutAsyncHandler?.Invoke(environment, accessToken, checkoutId, request, cancellationToken)
                ?? Task.FromResult(new SquareTerminalCheckoutRecord(checkoutId, environment));
        }

        public Task<SquarePaymentStatusDto?> GetPaymentAsync(string environment, string accessToken, string paymentId, CancellationToken cancellationToken)
        {
            return GetPaymentAsyncHandler?.Invoke(environment, accessToken, paymentId, cancellationToken)
                ?? Task.FromResult<SquarePaymentStatusDto?>(null);
        }

        public Task<SquareRefundResponse> CreateRefundAsync(string environment, string accessToken, SquareRefundRequest request, CancellationToken cancellationToken)
        {
            return CreateRefundAsyncHandler?.Invoke(environment, accessToken, request, cancellationToken)
                ?? Task.FromResult(new SquareRefundResponse("refund-default", environment));
        }

        public Task<SquareRefundResponse?> GetRefundAsync(string environment, string accessToken, string refundId, CancellationToken cancellationToken)
        {
            return GetRefundAsyncHandler?.Invoke(environment, accessToken, refundId, cancellationToken)
                ?? Task.FromResult<SquareRefundResponse?>(null);
        }
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenRefundUpdated_StoresRefundStatus()
    {
        var refundRepository = new InMemorySquareRefundRepository();
        const string notificationUrl = "https://example.com/api/v1/square/webhooks";
        var rawBody = CreateRefundUpdatedPayload("COMPLETED");
        var service = CreateRefundWebhookService(refundRepository);
        var signature = CreateSignature("sandbox-webhook-key", notificationUrl, rawBody);

        var response = await service.AcceptWebhookAsync(
            new SquareWebhookRequest(rawBody, signature, "Sandbox", notificationUrl),
            CancellationToken.None);

        Assert.Equal("accepted", response.Status);
        var stored = await refundRepository.GetRefundAsync("Sandbox", "refund-001", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("COMPLETED", stored!.Status);
        Assert.Equal("payment-001", stored.PaymentId);
        Assert.Equal(50, stored.Amount);
        Assert.Equal("AUD", stored.Currency);
        Assert.Equal("event-refund-001", stored.LastEventId);
    }

    [Fact]
    public async Task AcceptWebhookAsync_WhenRefundPayloadHasNoRefund_ThrowsPayloadInvalid()
    {
        const string notificationUrl = "https://example.com/api/v1/square/webhooks";
        const string rawBody = """{"event_id":"event-refund-bad","type":"refund.updated","data":{"object":{}}}""";
        var service = CreateRefundWebhookService(new InMemorySquareRefundRepository());
        var signature = CreateSignature("sandbox-webhook-key", notificationUrl, rawBody);

        var exception = await Assert.ThrowsAsync<SquareTerminalBackendException>(() =>
            service.AcceptWebhookAsync(
                new SquareWebhookRequest(rawBody, signature, "Sandbox", notificationUrl),
                CancellationToken.None));

        Assert.Equal("SQUARE_WEBHOOK_PAYLOAD_INVALID", exception.Code);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task GetRefundAsync_WhenSquareLookupFails_FallsBackToWebhookStoredRefund()
    {
        var refundRepository = new InMemorySquareRefundRepository();
        await refundRepository.UpsertRefundAsync(
            new SquareRefundRecord
            {
                Environment = "Sandbox",
                RefundId = "refund-001",
                Status = "COMPLETED",
                PaymentId = "payment-001",
                Amount = 50,
                Currency = "AUD",
                UpdatedAt = new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero)
            },
            CancellationToken.None);
        var service = CreateRefundLookupService(
            (_, _, _, _) => throw new SquareTerminalRestException(HttpStatusCode.BadGateway, "Square refund lookup failed."),
            refundRepository);

        var refund = await service.GetRefundAsync("sandbox", "refund-001", CancellationToken.None);

        Assert.NotNull(refund);
        Assert.Equal("refund-001", refund!.RefundId);
        Assert.Equal("COMPLETED", refund.Status);
        Assert.Equal("payment-001", refund.PaymentId);
        Assert.Equal(50, refund.AmountMoney?.Amount);
        Assert.Equal("AUD", refund.AmountMoney?.Currency);
    }

    [Fact]
    public async Task GetRefundAsync_WhenSquareLookupFailsAndNoStoredRefund_RethrowsOriginalError()
    {
        var service = CreateRefundLookupService(
            (_, _, _, _) => throw new SquareTerminalRestException(HttpStatusCode.BadGateway, "Square refund lookup failed."),
            new InMemorySquareRefundRepository());

        var exception = await Assert.ThrowsAsync<SquareTerminalRestException>(() =>
            service.GetRefundAsync("sandbox", "refund-001", CancellationToken.None));

        Assert.Equal("Square refund lookup failed.", exception.Message);
    }

    [Fact]
    public async Task GetRefundAsync_WhenSquareLookupSucceeds_PrefersLiveStatusOverStoredWebhook()
    {
        var refundRepository = new InMemorySquareRefundRepository();
        await refundRepository.UpsertRefundAsync(
            new SquareRefundRecord { Environment = "Sandbox", RefundId = "refund-001", Status = "PENDING" },
            CancellationToken.None);
        var service = CreateRefundLookupService(
            (environment, _, refundId, _) => Task.FromResult<SquareRefundResponse?>(
                new SquareRefundResponse(refundId, environment, Status: "COMPLETED")),
            refundRepository);

        var refund = await service.GetRefundAsync("sandbox", "refund-001", CancellationToken.None);

        Assert.Equal("COMPLETED", refund?.Status);
    }

    private static SquareTerminalBackendService CreateRefundWebhookService(ISquareRefundRepository refundRepository)
    {
        return new SquareTerminalBackendService(
            new RecordingSquareTokenService(response: null),
            new FakeSquareTerminalRestClient(),
            new SquareWebhookVerifier(),
            CreateWebhookOptions("sandbox-webhook-key"),
            new InMemorySquareCheckoutSessionRepository(),
            refundRepository);
    }

    private static SquareTerminalBackendService CreateRefundLookupService(
        Func<string, string, string, CancellationToken, Task<SquareRefundResponse?>> getRefund,
        ISquareRefundRepository refundRepository)
    {
        return new SquareTerminalBackendService(
            new RecordingSquareTokenService(new SquareTokenResponse("Sandbox", "token-001", DateTimeOffset.UtcNow)),
            new FakeSquareTerminalRestClient { GetRefundAsyncHandler = getRefund },
            refundRepository: refundRepository);
    }

    private static string CreateRefundUpdatedPayload(string status)
    {
        return $$"""
            {
              "event_id": "event-refund-001",
              "type": "refund.updated",
              "created_at": "2026-06-22T01:02:03Z",
              "data": {
                "object": {
                  "refund": {
                    "id": "refund-001",
                    "status": "{{status}}",
                    "payment_id": "payment-001",
                    "amount_money": {
                      "amount": 50,
                      "currency": "AUD"
                    },
                    "updated_at": "2026-06-22T01:02:03Z"
                  }
                }
              }
            }
            """;
    }

    private sealed class InMemorySquareRefundRepository : ISquareRefundRepository
    {
        private readonly Dictionary<string, SquareRefundRecord> _refunds = new(StringComparer.Ordinal);

        public Task UpsertRefundAsync(SquareRefundRecord refund, CancellationToken cancellationToken)
        {
            _refunds[$"{refund.Environment}::{refund.RefundId}"] = refund;
            return Task.CompletedTask;
        }

        public Task<SquareRefundRecord?> GetRefundAsync(
            string environment,
            string refundId,
            CancellationToken cancellationToken)
        {
            _refunds.TryGetValue($"{environment}::{refundId}", out var refund);
            return Task.FromResult(refund);
        }
    }

    private static SquareTerminalBackendService CreateWebhookService(
        ISquareCheckoutSessionRepository repository,
        IOptions<SquareWebhookOptions> options)
    {
        return new SquareTerminalBackendService(
            new RecordingSquareTokenService(response: null),
            new FakeSquareTerminalRestClient(),
            new SquareWebhookVerifier(),
            options,
            repository);
    }

    private static IOptions<SquareWebhookOptions> CreateWebhookOptions(
        string sandboxSignatureKey,
        string? sandboxNotificationUrl = null)
    {
        return Options.Create(new SquareWebhookOptions
        {
            WebhookSignatureKeys =
            {
                ["Sandbox"] = sandboxSignatureKey
            },
            WebhookNotificationUrls =
            {
                ["Sandbox"] = sandboxNotificationUrl
            }
        });
    }

    private static string CreateCheckoutUpdatedPayload(
        string eventId = "event-001",
        string createdAt = "2026-06-22T01:02:03Z",
        string status = "COMPLETED")
    {
        return $$"""
            {
              "event_id": "{{eventId}}",
              "type": "terminal.checkout.updated",
              "created_at": "{{createdAt}}",
              "data": {
                "object": {
                  "checkout": {
                    "id": "checkout-001",
                    "status": "{{status}}",
                    "amount_money": {
                      "amount": 1299,
                      "currency": "AUD"
                    },
                    "device_options": {
                      "device_id": "device-001"
                    },
                    "location_id": "location-001",
                    "payment_ids": [
                      "payment-001"
                    ]
                  }
                }
              }
            }
            """;
    }

    private static string CreateSignature(string signatureKey, string notificationUrl, string rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signatureKey));
        var payload = Encoding.UTF8.GetBytes(notificationUrl + rawBody);
        return Convert.ToBase64String(hmac.ComputeHash(payload));
    }

    private sealed class InMemorySquareCheckoutSessionRepository : ISquareCheckoutSessionRepository
    {
        public List<SquareWebhookEventRecord> WebhookEvents { get; } = [];

        public Dictionary<string, SquareCheckoutSessionRecord> Sessions { get; } = new(StringComparer.Ordinal);

        public int UpsertCheckoutCallCount { get; private set; }

        public Task<SquareCheckoutSessionRecord?> BindCheckoutOriginAsync(
            string environment,
            string checkoutId,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken)
        {
            Sessions.TryGetValue($"{environment}::{checkoutId}", out var session);
            if (session is not null)
            {
                session.OriginStoreCode ??= originStoreCode;
                session.OriginDeviceCode ??= originDeviceCode;
            }

            return Task.FromResult(session);
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionAsync(
            string environment,
            string checkoutId,
            CancellationToken cancellationToken)
        {
            Sessions.TryGetValue($"{environment}::{checkoutId}", out var session);
            return Task.FromResult(session);
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionByPaymentIdAsync(
            string environment,
            string paymentId,
            CancellationToken cancellationToken)
        {
            var session = Sessions.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(candidate.PaymentId, paymentId, StringComparison.OrdinalIgnoreCase) ||
                 candidate.PaymentIdsJson?.Contains(paymentId, StringComparison.OrdinalIgnoreCase) == true));
            return Task.FromResult(session);
        }

        public Task<bool> TryAddWebhookEventAsync(
            SquareWebhookEventRecord webhookEvent,
            CancellationToken cancellationToken)
        {
            var exists = WebhookEvents.Any(existing =>
                string.Equals(existing.Environment, webhookEvent.Environment, StringComparison.Ordinal) &&
                string.Equals(existing.EventId, webhookEvent.EventId, StringComparison.Ordinal));
            if (exists)
            {
                return Task.FromResult(false);
            }

            WebhookEvents.Add(webhookEvent);
            return Task.FromResult(true);
        }

        public Task UpsertCheckoutSessionAsync(
            SquareCheckoutSessionRecord session,
            CancellationToken cancellationToken)
        {
            UpsertCheckoutCallCount++;
            var key = $"{session.Environment}::{session.CheckoutId}";
            if (!Sessions.TryGetValue(key, out var existing))
            {
                Sessions[key] = session;
                return Task.CompletedTask;
            }

            // 测试仓库跟生产 SQL 规则保持一致，确保乱序 webhook 不会把终态回退掉。
            if (session.UpdatedAt < existing.UpdatedAt)
            {
                return Task.CompletedTask;
            }

            if (IsTerminal(existing.Status) && !IsTerminal(session.Status))
            {
                return Task.CompletedTask;
            }

            Sessions[key] = session;
            return Task.CompletedTask;
        }

        public Task<bool> RefreshCheckoutSessionFromLookupAsync(
            SquareCheckoutSessionRecord session,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        private static bool IsTerminal(string? status)
        {
            return status is not null &&
                (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase));
        }
    }
}
