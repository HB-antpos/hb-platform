using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

[Collection(GlobalLoggingTestCollection.Name)]
public sealed class LinklyCloudApiClientTests
{
    [Fact]
    public async Task PairAsync_posts_credentials_and_reads_secret()
    {
        using var logs = new ConsoleLogCapture();
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse("""{ "secret": "paired-secret" }""");
        })));

        var secret = await client.PairAsync(
            "https://auth.example/v1/",
            "store-user",
            "store-password",
            "12345");

        Assert.Equal("paired-secret", secret);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://auth.example/v1/pairing/cloudpos", capturedRequest!.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("store-user", ReadJsonString(body, "username"));
        Assert.Equal("store-password", ReadJsonString(body, "password"));
        Assert.Equal("12345", ReadJsonString(body, "pairCode"));
        using var requestLog = FindLinklyLog(logs.Lines, "pair", "request");
        Assert.Equal("cloud-api", requestLog.RootElement.GetProperty("source").GetString());
        Assert.Equal("request", requestLog.RootElement.GetProperty("direction").GetString());
        Assert.Equal("https://auth.example/v1/", requestLog.RootElement.GetProperty("details").GetProperty("authBaseUrl").GetString());
        // 用户名也不进日志，只记是否填写。
        Assert.True(requestLog.RootElement.GetProperty("request").GetProperty("hasUsername").GetBoolean());
        Assert.False(requestLog.RootElement.GetProperty("request").TryGetProperty("username", out _));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("store-user", StringComparison.Ordinal));
        Assert.Equal("<redacted>", requestLog.RootElement.GetProperty("request").GetProperty("password").GetString());
        Assert.Equal("<redacted>", requestLog.RootElement.GetProperty("request").GetProperty("pairCode").GetString());
        using var successLog = FindLinklyLog(logs.Lines, "pair", "succeeded");
        Assert.Equal("<redacted>", successLog.RootElement.GetProperty("response").GetProperty("secret").GetString());
    }

    [Fact]
    public async Task PairAsync_uses_development_sandbox_pairing_endpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse("""{ "secret": "paired-secret" }""");
        })));

        await client.PairAsync(
            CardTerminalSettings.GetLinklyCloudAuthBaseUrl(CardTerminalEnvironment.Sandbox),
            "sandbox-user",
            "sandbox-password",
            "123456");

        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "https://auth.sandbox.cloud.pceftpos.com/v1/pairing/cloudpos",
            capturedRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetTokenAsync_posts_secret_and_pos_identity()
    {
        using var logs = new ConsoleLogCapture();
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse("""{ "token": "bearer-token", "expirySeconds": 60 }""");
        })));

        var token = await client.GetTokenAsync(
            CreateCloudSettings() with
            {
                Environment = CardTerminalEnvironment.Production,
                LinklyPosVendorId = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22"
            },
            "3e7f5001-58a3-43fa-9129-6e84a7b4f2a0");

        Assert.Equal("bearer-token", token.Token);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://auth.example/v1/tokens/cloudpos", capturedRequest!.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("paired-secret", ReadJsonString(body, "secret"));
        Assert.Equal("HotBargainPOS", ReadJsonString(body, "posName"));
        Assert.Equal("2026.5.1", ReadJsonString(body, "posVersion"));
        Assert.Equal("3e7f5001-58a3-43fa-9129-6e84a7b4f2a0", ReadJsonString(body, "posId"));
        Assert.Equal(LinklyCloudIdentityConstants.ProductionPosVendorId, ReadJsonString(body, "posVendorId"));
        using var requestLog = FindLinklyLog(logs.Lines, "token", "request");
        Assert.Equal("<redacted>", requestLog.RootElement.GetProperty("request").GetProperty("secret").GetString());
        Assert.Equal("3e7f5001-58a3-43fa-9129-6e84a7b4f2a0", requestLog.RootElement.GetProperty("request").GetProperty("posId").GetString());
        using var successLog = FindLinklyLog(logs.Lines, "token", "succeeded");
        Assert.Equal("<redacted>", successLog.RootElement.GetProperty("response").GetProperty("token").GetString());
    }

    [Fact]
    public async Task Cloud_api_logs_redact_credentials_and_card_data_but_keep_business_references()
    {
        using var logs = new ConsoleLogCapture();
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/transaction", StringComparison.Ordinal)
                ? JsonResponse(
                    """
                    { "SessionId": "session-1", "Response": { "Success": true, "ResponseCode": "00", "Pan": "4111111111111234", "CardNumber": "5555555555554444", "Track2": "4111111111111234=2512", "CVV": "123" } }
                    """)
                : JsonResponse("""{ "token": "bearer-token", "expirySeconds": 60 }""");
        })));
        var settings = CreateCloudSettings() with
        {
            LinklyPosVendorId = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22"
        };

        _ = await client.GetTokenAsync(settings, "3e7f5001-58a3-43fa-9129-6e84a7b4f2a0");
        _ = await client.SendTransactionAsync(
            settings,
            "bearer-token",
            new LinklyCloudTransactionRequest("P", 1000, "RRN-260601120001"),
            "session-1");

        var text = string.Join(Environment.NewLine, logs.Lines);
        Assert.DoesNotContain("paired-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111234", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5555555555554444", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=2512", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"123\"", text, StringComparison.Ordinal);
        Assert.Contains("****1234", text, StringComparison.Ordinal);
        Assert.Contains("****4444", text, StringComparison.Ordinal);
        Assert.Contains("RRN-260601120001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloud_api_logs_sanitize_non_json_4xx_response_without_losing_business_reference()
    {
        using var logs = new ConsoleLogCapture();
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(
                HttpStatusCode.BadRequest,
                "Error=declined; refreshToken=refresh-token-secret; Track2=4111111111111111=2512; CVV=987; CardNumber=5555555555554444; RRN=260601120001"))));

        _ = await Assert.ThrowsAsync<LinklyCloudApiException>(() => client.SendTransactionAsync(
            CreateCloudSettings(),
            "request-token-secret",
            new LinklyCloudTransactionRequest("P", 1000, "RRN-260601120001"),
            "session-1"));

        var text = string.Join(Environment.NewLine, logs.Lines);
        Assert.DoesNotContain("request-token-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=2512", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5555555555554444", text, StringComparison.Ordinal);
        using var responseLog = FindLinklyLog(logs.Lines, "transaction", "response");
        var sanitizedResponse = responseLog.RootElement.GetProperty("response").GetString();
        Assert.DoesNotContain("987", sanitizedResponse, StringComparison.Ordinal);
        Assert.Contains("****4444", sanitizedResponse, StringComparison.Ordinal);
        Assert.Contains("260601120001", sanitizedResponse, StringComparison.Ordinal);
    }

    [Fact]
    public void Linkly_json_log_preserves_only_single_reference_scalars_and_masks_embedded_pan()
    {
        var scalarLog = LinklyJsonLog.Build(
            "test",
            "transaction",
            "response",
            details: new { TxnRef = "260601120001" });
        using var scalarDocument = JsonDocument.Parse(scalarLog);

        Assert.Equal(
            "260601120001",
            scalarDocument.RootElement.GetProperty("details").GetProperty("txnRef").GetString());

        var compoundLog = LinklyJsonLog.Build(
            "test",
            "transaction",
            "response",
            details: new { TxnRef = "123456789012 PAN 4111111111111111" });

        Assert.DoesNotContain("4111111111111111", compoundLog, StringComparison.Ordinal);
        Assert.Contains("****1111", compoundLog, StringComparison.Ordinal);
        Assert.Equal("<redacted>", LinklyJsonLog.MaskCardNumberForLog("123"));
    }

    [Theory]
    [InlineData("not-a-uuid", "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22", false, "Linkly POS id must be a UUID v4.")]
    [InlineData("3e7f5001-58a3-33fa-9129-6e84a7b4f2a0", "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22", false, "Linkly POS id must be a UUID v4.")]
    [InlineData("3e7f5001-58a3-43fa-9129-6e84a7b4f2a0", "not-a-uuid", true, "Linkly POS vendor id must be a UUID v4.")]
    [InlineData("3e7f5001-58a3-43fa-9129-6e84a7b4f2a0", "a256b7ec-709d-3c7d-8ffe-57cc7ca1fd22", true, "Linkly POS vendor id must be a UUID v4.")]
    public async Task GetTokenAsync_requires_pos_identity_uuid_v4(
        string posId,
        string posVendorId,
        bool sandbox,
        string expectedMessage)
    {
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("""{ "token": "bearer-token", "expirySeconds": 60 }"""))));
        var settings = CreateCloudSettings() with
        {
            Environment = sandbox ? CardTerminalEnvironment.Sandbox : CardTerminalEnvironment.Production,
            LinklyPosVendorId = posVendorId
        };

        var exception = await Assert.ThrowsAsync<LinklyCloudApiException>(() =>
            client.GetTokenAsync(settings, posId));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task GetTokenAsync_preserves_configured_sandbox_vendor_id()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse("""{ "token": "bearer-token", "expirySeconds": 60 }""");
        })));
        var settings = CreateCloudSettings() with
        {
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyPosVendorId = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22"
        };

        await client.GetTokenAsync(settings, "3e7f5001-58a3-43fa-9129-6e84a7b4f2a0");

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        Assert.Equal(settings.LinklyPosVendorId, ReadJsonString(body, "posVendorId"));
    }

    [Fact]
    public async Task SendStatusAsync_posts_status_request_and_parses_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "SessionId": "session-1",
                  "ResponseType": "status",
                  "Response": {
                    "Success": true,
                    "ResponseCode": "00",
                    "ResponseText": "READY",
                    "LoggedOn": true,
                    "Catid": "TERM",
                    "Caid": "MERCHANT"
                  }
                }
                """);
        })));

        var result = await client.SendStatusAsync(CreateCloudSettings(), "bearer-token");

        Assert.True(result.Succeeded);
        Assert.Equal("00", result.ResponseCode);
        Assert.Equal("READY", result.ResponseText);
        Assert.True(result.LoggedOn);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Contains("/status?async=false", capturedRequest.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("bearer-token", capturedRequest.Headers.Authorization?.Parameter);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("0", ReadNestedJsonString(body, "Request", "StatusType"));
    }

    [Fact]
    public async Task SendLogonAsync_posts_logon_request_and_parses_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "SessionId": "session-1",
                  "ResponseType": "logon",
                  "Response": {
                    "Success": true,
                    "ResponseCode": "00",
                    "ResponseText": "APPROVED",
                    "Catid": "TERM",
                    "Caid": "MERCHANT",
                    "PinPadVersion": "100800"
                  }
                }
                """);
        })));

        var result = await client.SendLogonAsync(CreateCloudSettings(), "bearer-token");

        Assert.True(result.Succeeded);
        Assert.Equal("00", result.ResponseCode);
        Assert.Equal("APPROVED", result.ResponseText);
        Assert.Equal("TERM", result.Catid);
        Assert.Equal("MERCHANT", result.Caid);
        Assert.Equal("100800", result.PinPadVersion);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Contains("/logon?async=false", capturedRequest.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("bearer-token", capturedRequest.Headers.Authorization?.Parameter);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("00", ReadNestedJsonString(body, "Request", "Merchant"));
        Assert.Equal(" ", ReadNestedJsonString(body, "Request", "LogonType"));
        Assert.Equal("00", ReadNestedJsonString(body, "Request", "Application"));
        Assert.Equal("0", ReadNestedJsonString(body, "Request", "ReceiptAutoPrint"));
        Assert.Equal("0", ReadNestedJsonString(body, "Request", "CutReceipt"));
    }

    [Fact]
    public async Task SendTransactionAsync_posts_purchase_and_parses_transaction_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "SessionId": "session-1",
                  "ResponseType": "transaction",
                  "Response": {
                    "Success": true,
                    "ResponseCode": "00",
                    "ResponseText": "APPROVED",
                    "TxnRef": "TXN-1",
                    "AmtPurchase": 1000,
                    "AuthCode": 123456,
                    "CardType": "VISA",
                    "Pan": "4111111111111234",
                    "Caid": "MID",
                    "Stan": 42,
                    "PurchaseAnalysisData": { "RFN": "RFN-1" }
                  }
                }
                """);
        })));

        var result = await client.SendTransactionAsync(
            CreateCloudSettings(),
            "bearer-token",
            new LinklyCloudTransactionRequest("P", 1000, "TXN-1"),
            "session-1");

        Assert.True(result.Succeeded);
        Assert.Equal("TXN-1", result.TxnRef);
        Assert.Equal("123456", result.AuthCode);
        Assert.Equal("VISA", result.CardType);
        Assert.Equal("4111111111111234", result.Pan);
        Assert.Equal(10m, result.Amount);
        Assert.Equal("RFN-1", result.RefundReference);
        Assert.NotNull(capturedRequest);
        Assert.Contains("/transaction?async=false", capturedRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("P", ReadNestedJsonString(body, "Request", "TxnType"));
        Assert.Equal("1000", ReadNestedJsonString(body, "Request", "AmtPurchase"));
        Assert.Equal("TXN-1", ReadNestedJsonString(body, "Request", "TxnRef"));
        // 同步模式官方不支持 POS 端收据打印：交易必须让 PIN pad 打印银行小票（与直连结算同值），不能是 "0"。
        Assert.Equal("7", ReadNestedJsonString(body, "Request", "ReceiptAutoPrint"));
    }

    [Fact]
    public async Task SendTransactionAsync_serializes_purchase_analysis_data()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse("""{ "SessionId": "session-1", "Response": { "Success": true, "TxnRef": "TXN-1" } }""");
        })));

        await client.SendTransactionAsync(
            CreateCloudSettings(),
            "bearer-token",
            new LinklyCloudTransactionRequest(
                "P",
                1000,
                "TXN-1",
                new Dictionary<string, string> { ["RFN"] = "TXN-1", ["AMT"] = "000001000", ["PCM"] = "0000" }),
            "session-1");

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var pad = document.RootElement.GetProperty("Request").GetProperty("PurchaseAnalysisData");
        Assert.Equal("TXN-1", pad.GetProperty("RFN").GetString());
        Assert.Equal("000001000", pad.GetProperty("AMT").GetString());
        Assert.Equal("0000", pad.GetProperty("PCM").GetString());
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("Wed, 21 Oct 2099 07:28:00 GMT", -1)]
    public async Task GetTransactionAsync_surfaces_rate_limit_with_retry_after(string retryAfter, int expectedSeconds)
    {
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
        {
            var response = TextResponse(HttpStatusCode.TooManyRequests, "slow down");
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        })));

        var exception = await Assert.ThrowsAsync<LinklyCloudApiException>(() =>
            client.GetTransactionAsync(CreateCloudSettings(), "bearer-token", "session-1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.False(exception.IsAuthenticationFailure);
        Assert.NotNull(exception.RetryAfter);
        if (expectedSeconds >= 0)
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), exception.RetryAfter);
        }
        else
        {
            Assert.True(exception.RetryAfter > TimeSpan.FromDays(1));
        }
    }

    [Fact]
    public async Task GetTransactionAsync_rate_limit_without_retry_after_has_no_retry_after()
    {
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(HttpStatusCode.TooManyRequests, string.Empty))));

        var exception = await Assert.ThrowsAsync<LinklyCloudApiException>(() =>
            client.GetTransactionAsync(CreateCloudSettings(), "bearer-token", "session-1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public async Task Cloud_api_direct_operation_logs_never_contain_bearer_token_or_secret()
    {
        using var logs = new ConsoleLogCapture();
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sendkey", StringComparison.Ordinal))
            {
                return JsonResponse("""{ "success": true }""");
            }

            return JsonResponse("""{ "SessionId": "session-1", "Response": { "Success": true, "ResponseCode": "00", "TxnRef": "TXN-1", "AmtPurchase": 1000 } }""");
        })));
        var settings = CreateCloudSettings();

        _ = await client.SendStatusAsync(settings, "very-secret-bearer");
        _ = await client.SendLogonAsync(settings, "very-secret-bearer");
        _ = await client.SendTransactionAsync(settings, "very-secret-bearer", new LinklyCloudTransactionRequest("P", 1000, "TXN-1"), "session-1");
        _ = await client.GetTransactionAsync(settings, "very-secret-bearer", "session-1");
        _ = await client.SendSettlementAsync(settings, "very-secret-bearer", "session-1");
        await client.SendKeyAsync(settings, "very-secret-bearer", "session-1", "OK/CANCEL", null);

        var text = string.Join(Environment.NewLine, logs.Lines);
        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain("very-secret-bearer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("paired-secret", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetTransactionAsync_treats_recoverable_status_as_pending(HttpStatusCode statusCode)
    {
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(statusCode, string.Empty))));

        var result = await client.GetTransactionAsync(CreateCloudSettings(), "bearer-token", "session-1");

        Assert.Equal("session-1", result.SessionId);
        Assert.Equal(LinklyCloudTransactionOutcome.Pending, result.Outcome);
    }

    [Fact]
    public async Task GetTransactionAsync_treats_not_found_as_not_submitted()
    {
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
            TextResponse(HttpStatusCode.NotFound, string.Empty))));

        var result = await client.GetTransactionAsync(CreateCloudSettings(), "bearer-token", "session-1");

        Assert.Equal("session-1", result.SessionId);
        Assert.Equal(LinklyCloudTransactionOutcome.NotSubmitted, result.Outcome);
    }

    [Fact]
    public async Task SendSettlementAsync_posts_official_settlement_fields_and_parses_receipt()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "SessionId": "settlement-1",
                  "Response": {
                    "Success": true,
                    "ResponseCode": "00",
                    "ResponseText": "SETTLED",
                    "SettlementData": "TOTAL=10.00",
                    "ReceiptText": ["SETTLEMENT", "TOTAL $10.00"]
                  }
                }
                """);
        })));

        var result = await client.SendSettlementAsync(CreateCloudSettings(), "bearer-token", "settlement-1");

        Assert.True(result.Succeeded);
        Assert.Equal("TOTAL=10.00", result.SettlementData);
        Assert.Equal(["SETTLEMENT" + Environment.NewLine + "TOTAL $10.00"], result.ReceiptTexts);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://rest.example/v1/sessions/settlement-1/settlement?async=false", capturedRequest.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("00", ReadNestedJsonString(body, "Request", "Merchant"));
        Assert.Equal("00", ReadNestedJsonString(body, "Request", "Application"));
        Assert.Equal("S", ReadNestedJsonString(body, "Request", "SettlementType"));
        Assert.Equal("7", ReadNestedJsonString(body, "Request", "ReceiptAutoPrint"));
        Assert.Equal("0", ReadNestedJsonString(body, "Request", "CutReceipt"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendSettlementAsync_treats_uncertain_http_status_as_unknown(HttpStatusCode statusCode)
    {
        var requestCount = 0;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return TextResponse(statusCode, string.Empty);
        })));

        var result = await client.SendSettlementAsync(CreateCloudSettings(), "bearer-token", "settlement-1");

        Assert.Equal("settlement-1", result.SessionId);
        Assert.Equal(LinklyCloudSettlementOutcome.Unknown, result.Outcome);
        Assert.Equal(1, requestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SendSettlementAsync_preserves_definitive_4xx_status(HttpStatusCode statusCode)
    {
        var requestCount = 0;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return TextResponse(statusCode, string.Empty);
        })));

        var exception = await Assert.ThrowsAsync<LinklyCloudApiException>(() =>
            client.SendSettlementAsync(CreateCloudSettings(), "bearer-token", "settlement-1"));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task SendSettlementAsync_treats_http_exception_as_unknown()
    {
        var requestCount = 0;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            throw new HttpRequestException("connection lost");
        })));

        var result = await client.SendSettlementAsync(CreateCloudSettings(), "bearer-token", "settlement-1");

        Assert.Equal(LinklyCloudSettlementOutcome.Unknown, result.Outcome);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task SendKeyAsync_posts_sendkey_request_for_existing_session()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = new LinklyCloudApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = CloneRequestWithBody(request);
            return JsonResponse("""{ "success": true }""");
        })));

        await client.SendKeyAsync(
            CreateCloudSettings(),
            "bearer-token",
            "session-1",
            "OK/CANCEL",
            null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://rest.example/v1/sessions/session-1/sendkey?async=false", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("bearer-token", capturedRequest.Headers.Authorization?.Parameter);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, ReadNestedJsonString(body, "Request", "Key"));
    }

    private static CardTerminalSettings CreateCloudSettings()
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            LinklyCloudSecret = "paired-secret",
            LinklyCloudAuthBaseUrl = "https://auth.example/v1/",
            LinklyCloudRestBaseUrl = "https://rest.example/v1/",
            LinklyPosName = "HotBargainPOS",
            LinklyPosVersion = "2026.5.1",
            LinklyPosVendorId = "a256b7ec-709d-4c7d-8ffe-57cc7ca1fd22"
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string text)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain")
        };
    }

    private static HttpRequestMessage CloneRequestWithBody(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                Encoding.UTF8,
                "application/json");
        }

        return clone;
    }

    private static string? ReadJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).ValueKind == JsonValueKind.Number
            ? document.RootElement.GetProperty(propertyName).GetRawText()
            : document.RootElement.GetProperty(propertyName).GetString();
    }

    private static string? ReadNestedJsonString(string json, string objectName, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.GetProperty(objectName).GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();
    }

    private static JsonDocument FindLinklyLog(
        IReadOnlyList<string> lines,
        string operation,
        string phase)
    {
        foreach (var line in lines)
        {
            var jsonStart = line.IndexOf('{', StringComparison.Ordinal);
            if (jsonStart < 0)
            {
                continue;
            }

            var document = JsonDocument.Parse(line[jsonStart..]);
            if (string.Equals(document.RootElement.GetProperty("operation").GetString(), operation, StringComparison.Ordinal) &&
                string.Equals(document.RootElement.GetProperty("phase").GetString(), phase, StringComparison.Ordinal))
            {
                return document;
            }

            document.Dispose();
        }

        throw new Xunit.Sdk.XunitException($"Expected Linkly JSON log operation={operation} phase={phase}.");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
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
