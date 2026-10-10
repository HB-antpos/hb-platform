using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Square;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class SquareTerminalRestClientTests
{
    [Fact]
    public async Task GetRefundAsync_SendsExpectedSandboxRequest()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "refund": {
                "id": "refund-1",
                "status": "COMPLETED",
                "payment_id": "payment-1",
                "amount_money": { "amount": 99, "currency": "AUD" },
                "created_at": "2026-08-17T04:50:10Z",
                "updated_at": "2026-08-17T04:53:22Z"
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(
            httpClient,
            Options.Create(new SquareTerminalRestOptions()));

        var refund = await client.GetRefundAsync(
            "Sandbox",
            "secret-square-token",
            "refund-1",
            CancellationToken.None);

        Assert.NotNull(refund);
        Assert.Equal("COMPLETED", refund.Status);
        // 创建时间用于 POS 在退款回执丢失后按时间窗口找回退款。
        Assert.Equal(DateTimeOffset.Parse("2026-08-17T04:50:10Z"), refund.CreatedAt);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://connect.squareupsandbox.com/v2/refunds/refund-1",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task GetLocationsAsync_UsesConfiguredSquareVersionHeader()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "locations": [
                {
                  "id": "location-001",
                  "name": "Front Counter"
                }
              ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(
            httpClient,
            Options.Create(new SquareTerminalRestOptions
            {
                ApiVersion = "2026-05-20"
            }));

        await client.GetLocationsAsync("Production", "secret-square-token", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("2026-05-20", handler.LastRequest!.Headers.GetValues("Square-Version").Single());
    }

    [Fact]
    public void Constructor_RejectsInvalidSquareVersion()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => CreateJsonResponse("{}")));

        var exception = Assert.Throws<InvalidOperationException>(() => new HttpSquareTerminalRestClient(
            httpClient,
            Options.Create(new SquareTerminalRestOptions
            {
                ApiVersion = "20260520"
            })));

        Assert.Contains("Square:ApiVersion must use yyyy-MM-dd.", exception.Message);
    }

    [Fact]
    public async Task CreateCheckoutAsync_SendsExpectedProductionRequest()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "checkout": {
                "id": "checkout-001",
                "status": "PENDING",
                "device_options": {
                  "device_id": "device-001"
                },
                "location_id": "location-001",
                "cancel_reason": "SELLER_CANCELED",
                "amount_money": {
                  "amount": 1299,
                  "currency": "AUD"
                },
                "updated_at": "2026-06-22T01:02:03Z"
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);
        var request = new SquareCreateCheckoutRequest(
            "Production",
            "idem-001",
            "device-001",
            "location-001",
            new SquareMoneyDto(1299, "AUD"),
            ReferenceId: "reference-001",
            Note: "front counter",
            OrderId: "order-001");

        var response = await client.CreateCheckoutAsync("Production", "secret-square-token", request, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://connect.squareup.com/v2/terminals/checkouts",
            handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-square-token", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("2026-01-22", handler.LastRequest.Headers.GetValues("Square-Version").Single());

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("idem-001", document.RootElement.GetProperty("idempotency_key").GetString());
        var checkout = document.RootElement.GetProperty("checkout");
        Assert.Equal("device-001", checkout.GetProperty("device_options").GetProperty("device_id").GetString());
        Assert.Equal("location-001", checkout.GetProperty("location_id").GetString());
        Assert.Equal("reference-001", checkout.GetProperty("reference_id").GetString());
        Assert.Equal("front counter", checkout.GetProperty("note").GetString());
        Assert.Equal("order-001", checkout.GetProperty("order_id").GetString());
        Assert.Equal(1299, checkout.GetProperty("amount_money").GetProperty("amount").GetInt64());
        Assert.Equal("AUD", checkout.GetProperty("amount_money").GetProperty("currency").GetString());

        Assert.Equal("checkout-001", response.CheckoutId);
        Assert.Equal("PENDING", response.Status);
        Assert.Equal("device-001", response.DeviceId);
        Assert.Equal("location-001", response.LocationId);
        Assert.Equal("SELLER_CANCELED", response.CancelReason);
        Assert.Equal(1299, response.AmountMoney?.Amount);
    }

    [Fact]
    public async Task GetCheckoutAsync_MapsLegacyTopLevelDeviceIdAsFallback()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "checkout": {
                "id": "checkout-legacy",
                "status": "PENDING",
                "device_id": "legacy-device",
                "location_id": "location-legacy"
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);

        var response = await client.GetCheckoutAsync("Sandbox", "secret-square-token", "checkout-legacy", CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("legacy-device", response!.DeviceId);
        Assert.Equal("location-legacy", response.LocationId);
    }

    [Fact]
    public async Task GetPaymentAsync_SendsExpectedSandboxRequest()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "payment": {
                "id": "payment-001",
                "status": "COMPLETED",
                "approved_money": {
                  "amount": 1299,
                  "currency": "AUD"
                },
                "total_money": {
                  "amount": 1299,
                  "currency": "AUD"
                },
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
                },
                "refund_ids": [ "refund-a", "refund-b" ],
                "updated_at": "2026-06-22T01:02:03Z"
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);

        var response = await client.GetPaymentAsync("Sandbox", "secret-square-token", "payment-001", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal(
            "https://connect.squareupsandbox.com/v2/payments/payment-001",
            handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-square-token", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("2026-01-22", handler.LastRequest.Headers.GetValues("Square-Version").Single());

        Assert.NotNull(response);
        Assert.Equal("payment-001", response!.PaymentId);
        Assert.Equal("COMPLETED", response.Status);
        Assert.Equal(1299, response.ApprovedMoney?.Amount);
        Assert.Equal(1299, response.TotalMoney?.Amount);
        Assert.Equal("VISA", response.CardBrand);
        Assert.Equal("****1111", response.MaskedCardNumber);
        Assert.Equal("68aLBM", response.AuthCode);
        Assert.Equal(["refund-a", "refund-b"], response.RefundIds);
    }

    [Fact]
    public async Task GetDevicesAsync_MapsOfficialDeviceAttributesAndStatus()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "devices": [
                {
                  "id": "device:995CS397A6475287",
                  "attributes": {
                    "name": "Square Terminal 5287"
                  },
                  "components": [
                    {
                      "type": "APPLICATION",
                      "application_details": {
                        "application_type": "TERMINAL_API",
                        "session_location": "location-001"
                      }
                    }
                  ],
                  "status": {
                    "category": "AVAILABLE"
                  }
                }
              ]
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);

        var response = await client.GetDevicesAsync("Production", "secret-square-token", "location-001", CancellationToken.None);

        var device = Assert.Single(response);
        Assert.Equal("device:995CS397A6475287", device.Id);
        Assert.Equal("Square Terminal 5287", device.Name);
        Assert.Equal("AVAILABLE", device.Status);
        Assert.Equal("location-001", device.LocationId);
    }

    [Fact]
    public async Task CreateRefundAsync_SendsExpectedRefundRequest()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "refund": {
                "id": "refund-001",
                "status": "PENDING",
                "payment_id": "payment-001",
                "amount_money": {
                  "amount": 500,
                  "currency": "AUD"
                },
                "updated_at": "2026-06-22T01:02:03Z"
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);
        var request = new SquareRefundRequest(
            "Sandbox",
            "refund-idem-001",
            "payment-001",
            new SquareMoneyDto(500, "AUD"),
            Reason: "customer changed mind");

        var response = await client.CreateRefundAsync("Sandbox", "secret-square-token", request, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://connect.squareupsandbox.com/v2/refunds",
            handler.LastRequest.RequestUri!.AbsoluteUri);

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("refund-idem-001", document.RootElement.GetProperty("idempotency_key").GetString());
        Assert.Equal("payment-001", document.RootElement.GetProperty("payment_id").GetString());
        Assert.Equal("customer changed mind", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(500, document.RootElement.GetProperty("amount_money").GetProperty("amount").GetInt64());
        Assert.Equal("AUD", document.RootElement.GetProperty("amount_money").GetProperty("currency").GetString());

        Assert.Equal("refund-001", response.RefundId);
        Assert.Equal("PENDING", response.Status);
        Assert.Equal("payment-001", response.PaymentId);
    }

    [Fact]
    public async Task CreateDeviceCodeAsync_SendsTerminalApiPayload()
    {
        var handler = new CapturingHandler(_ => CreateJsonResponse(
            """
            {
              "device_code": {
                "id": "device-code-001",
                "code": "ABCDEF",
                "status": "UNPAIRED",
                "device_id": null,
                "location_id": "location-001",
                "name": "Front Counter"
              }
            }
            """));
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);
        var request = new SquareCreateDeviceCodeRequest(
            "Production",
            "device-code-idem-001",
            "location-001",
            Name: "Front Counter");

        var response = await client.CreateDeviceCodeAsync("Production", "secret-square-token", request, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://connect.squareup.com/v2/devices/codes",
            handler.LastRequest.RequestUri!.AbsoluteUri);

        using var document = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("device-code-idem-001", document.RootElement.GetProperty("idempotency_key").GetString());
        var deviceCode = document.RootElement.GetProperty("device_code");
        Assert.Equal("Front Counter", deviceCode.GetProperty("name").GetString());
        Assert.Equal("location-001", deviceCode.GetProperty("location_id").GetString());
        Assert.Equal("TERMINAL_API", deviceCode.GetProperty("product_type").GetString());

        Assert.Equal("device-code-001", response.Id);
        Assert.Equal("ABCDEF", response.Code);
        Assert.Equal("UNPAIRED", response.Status);
    }

    [Fact]
    public async Task GetLocationsAsync_ThrowsSanitizedSquareErrorWithoutLeakingToken()
    {
        const string accessToken = "secret-square-token";
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """
                {
                  "errors": [
                    {
                      "code": "UNAUTHORIZED",
                      "detail": "token secret-square-token is invalid"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);

        var exception = await Assert.ThrowsAsync<SquareTerminalRestException>(() =>
            client.GetLocationsAsync("Production", accessToken, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("UNAUTHORIZED", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLocationsAsync_RedactsTokenLikeValuesWhenExactAccessTokenIsAbsent()
    {
        const string bearerToken = "EAAA1234567890abcdefTOKEN";
        const string squareToken = "sq0atp-AbCdEf1234567890";
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $$"""
                {
                  "errors": [
                    {
                      "code": "UNAUTHORIZED",
                      "detail": "Authorization Bearer {{bearerToken}} failed; fallback {{squareToken}} was rejected"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new HttpSquareTerminalRestClient(httpClient);

        var exception = await Assert.ThrowsAsync<SquareTerminalRestException>(() =>
            client.GetLocationsAsync("Production", "configured-token-not-in-body", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(squareToken, exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responseFactory(request);
        }
    }
}
