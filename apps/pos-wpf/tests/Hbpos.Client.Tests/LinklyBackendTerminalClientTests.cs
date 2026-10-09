using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

[Collection(GlobalLoggingTestCollection.Name)]
public sealed class LinklyBackendTerminalClientTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "CLOUD-EVIDENCE-001")]
    [InlineData(true, null)]
    public async Task Cloud_payment_uses_sanitized_transaction_evidence(bool refund, string? cardTxnRef)
    {
        var submitted = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal))
                submitted++;
            return JsonResponse(SanitizedTransactionSessionJson(100, cardTxnRef));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = refund
            ? await client.RefundAsync(1m, CreateSession(), CreateSettings(), "ANZCLOUD:original:RFN-ORIGINAL")
            : await client.PurchaseAsync(1m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(1m, result.AuthorizedAmount);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("CLOUD-EVIDENCE-001", transaction.TxnRef);
        Assert.Equal("00", transaction.ResponseCode);
        Assert.Equal("APPROVED", transaction.ResponseText);
        Assert.Equal("AUTH-001", transaction.AuthCode);
        Assert.Equal("RFN-001", transaction.RefundReference);
        Assert.Equal(1m, transaction.Amount);
        Assert.Equal(1, submitted);
    }

    [Theory]
    [InlineData(99L, null, "CLOUD-EVIDENCE-001")]
    [InlineData(null, null, "CLOUD-EVIDENCE-001")]
    [InlineData(-100L, null, "CLOUD-EVIDENCE-001")]
    [InlineData(100L, "OTHER-TRANSACTION", "CLOUD-EVIDENCE-001")]
    [InlineData(100L, null, null)]
    public async Task Cloud_payment_keeps_unverifiable_sanitized_evidence_unknown(
        long? amountCents, string? cardTxnRef, string? sessionTxnRef)
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(SanitizedTransactionSessionJson(amountCents, cardTxnRef, sessionTxnRef)));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(1m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task Cloud_payment_keeps_final_decline_despite_approved_sanitized_evidence()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(SanitizedTransactionSessionJson(100, null, transactionSuccess: false)));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(1m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
    }

    [Theory]
    [InlineData("05", null)]
    [InlineData("00", "05")]
    public async Task Cloud_payment_keeps_conflicting_success_evidence_unknown(string cardCode, string? protectedCode)
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(SanitizedTransactionSessionJson(100, null, cardCode: cardCode, protectedCode: protectedCode)));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(1m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    private static string SanitizedTransactionSessionJson(
        long? amountCents, string? cardTxnRef,
        string? sessionTxnRef = "CLOUD-EVIDENCE-001", bool transactionSuccess = true,
        string cardCode = "00", string? protectedCode = null)
    {
        // 复现状态接口：成功位及安全明细齐全，外层响应码为空，不能由测试桩补造通知金额。
        return JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                environment = "Sandbox", storeCode = "S01", deviceCode = "TERM-1",
                sessionId = "cloud-evidence-session", status = "Completed", txnRef = sessionTxnRef,
                responseCode = protectedCode, responseText = (string?)null, transactionSuccess,
                lastHttpStatus = 200, notifications = Array.Empty<object>(),
                cardTransaction = new LinklyCloudBackendCardTransactionDto(
                    cardTxnRef, "RFN-001", "AUTH-001", "VISA", "************1111", "MID-001",
                    cardCode, "APPROVED", "123456", null, amountCents)
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Theory]
    [InlineData("pair", "en-US", "awaiting confirmation", "new pair code")]
    [InlineData("logon", "en-US", "awaiting confirmation", "new pair code")]
    [InlineData("status", "en-US", "awaiting confirmation", "new pair code")]
    [InlineData("pair", "zh-CN", "尚未由 POS 确认", "更换配对码不能解除占用")]
    [InlineData("logon", "zh-CN", "尚未由 POS 确认", "更换配对码不能解除占用")]
    [InlineData("status", "zh-CN", "尚未由 POS 确认", "更换配对码不能解除占用")]
    public async Task Setup_conflict_explains_unacknowledged_transaction_without_retrying(
        string operation, string culture, string reason, string nextStep)
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse(JsonSerializer.Serialize(new
            {
                success = false,
                errorCode = operation == "pair" ? "LINKLY_CLOUD_TERMINAL_SESSION_ACTIVE" : "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION",
                message = "An active Linkly Cloud transaction already exists for this terminal."
            }), HttpStatusCode.Conflict);
        });
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), localization);

        string? message;
        if (operation == "pair")
        {
            var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                client.PairTerminalAsync(CardTerminalEnvironment.Sandbox, Guid.NewGuid(), "123456"));
            Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
            message = exception.Message;
        }
        else
        {
            var result = operation == "logon"
                ? await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox)
                : await client.TestTransactionStatusAsync(CardTerminalEnvironment.Sandbox);
            Assert.False(result.Succeeded);
            message = result.Message;
        }

        Assert.Contains(reason, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nextStep, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", message, StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("LINKLY_CLOUD_BACKEND_PAIR_REJECTED", "new 6-digit pair code")]
    [InlineData("LINKLY_CLOUD_BACKEND_PAIR_REQUEST_INVALID", "terminal, environment and 6-digit pair code")]
    [InlineData("LINKLY_CLOUD_BACKEND_PAIR_TIMEOUT", "could not be confirmed")]
    [InlineData("LINKLY_CLOUD_BACKEND_PAIR_CREDENTIAL_MISSING", "Web Admin")]
    [InlineData("LINKLY_CLOUD_BACKEND_PAIR_IN_PROGRESS", "Wait for it to finish")]
    [InlineData("UNRECOGNIZED_ERROR", "Specific server explanation")]
    public async Task Pair_failure_distinguishes_rejection_timeout_credentials_and_busy(string errorCode, string expected)
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(JsonSerializer.Serialize(new
        {
            success = false, errorCode, message = "Specific server explanation"
        }), HttpStatusCode.BadRequest));
        var localization = new LocalizationService();
        localization.SetCulture("en-US");
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), localization);

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.PairTerminalAsync(CardTerminalEnvironment.Sandbox, Guid.NewGuid(), "123456"));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pair_failure_preserves_structured_error_code_for_settings_view_model()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(JsonSerializer.Serialize(new
        {
            success = false,
            errorCode = "LINKLY_CLOUD_BACKEND_PAIR_REJECTED",
            message = "Pairing rejected"
        }), HttpStatusCode.BadRequest));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var exception = await Assert.ThrowsAsync<LinklyBackendHttpException>(() =>
            client.PairTerminalAsync(CardTerminalEnvironment.Sandbox, Guid.NewGuid(), "123456"));

        Assert.Equal(HttpStatusCode.BadRequest, exception.HttpStatus);
        Assert.Equal("LINKLY_CLOUD_BACKEND_PAIR_REJECTED", exception.ErrorCode);
    }

    [Fact]
    public async Task GetTerminalsAsync_returns_safe_terminal_directory_and_caches_selection()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/v1/linkly/cloud-backend/terminals?environment=Sandbox",
                request.RequestUri!.PathAndQuery);
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "mode": "Active",
                    "selectedTerminalId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "selectionRevision": 7,
                    "terminals": [
                      {
                        "terminalId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                        "laneNo": 1,
                        "displayName": "Front Counter",
                        "pairingState": "Ready",
                        "isBusy": false,
                        "isReady": true,
                        "lastHealthStatus": "Healthy",
                        "lastHealthAt": "2026-09-02T00:00:00Z"
                      }
                    ]
                  }
                }
                """);
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.GetTerminalsAsync(CardTerminalEnvironment.Sandbox);

        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), result.SelectedTerminalId);
        Assert.Equal("Active", result.Mode);
        Assert.Equal(7, result.SelectionRevision);
        var terminal = Assert.Single(result.Terminals);
        Assert.Equal(1, terminal.LaneNo);
        Assert.Equal("Front Counter", terminal.DisplayName);
        Assert.True(terminal.IsReady);
    }

    [Fact]
    public async Task SelectTerminalAsync_puts_expected_revision_and_updates_cached_selection()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "terminalId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    "revision": 8
                  }
                }
                """);
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SelectTerminalAsync(
            CardTerminalEnvironment.Sandbox,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            expectedRevision: 7);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured.Method);
        Assert.Equal("/api/v1/linkly/cloud-backend/terminal-selection", captured.RequestUri!.AbsolutePath);
        var body = await captured.Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(body, "environment"));
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", ReadJsonString(body, "terminalId"));
        Assert.Equal("7", ReadJsonString(body, "expectedRevision"));
        Assert.Equal(8, result.Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectTerminalAsync_rejects_mismatched_identity_and_clears_payment_cache(bool wrongEnvironment)
    {
        var cachedId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var requestedId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        HttpRequestMessage? logonRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    success = true,
                    data = new LinklyCloudTerminalListResponse(
                        "Sandbox", cachedId, 7,
                        [new LinklyCloudTerminalSummary(cachedId, 1, "Front", "Ready", false, true, null, null)],
                        "Active")
                }));
            }
            if (request.Method == HttpMethod.Put)
            {
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    success = true,
                    data = new LinklyCloudTerminalSelectionResponse(
                        wrongEnvironment ? "Production" : "Sandbox",
                        wrongEnvironment ? requestedId : Guid.NewGuid(),
                        8)
                }));
            }

            logonRequest = CloneRequestWithBody(request);
            return JsonResponse(JsonSerializer.Serialize(new
            {
                success = true,
                data = new LinklyCloudBackendLogonTestResponse(
                    "Sandbox", "S01", "POS-1", "test", DateTimeOffset.UtcNow,
                    200, true, "00", "APPROVED", null, null, null, "connected")
            }));
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());
        await client.GetTerminalsAsync(CardTerminalEnvironment.Sandbox);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SelectTerminalAsync(
            CardTerminalEnvironment.Sandbox, requestedId, expectedRevision: 7));
        await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.NotNull(logonRequest);
        Assert.DoesNotContain("terminalId=", logonRequest.RequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("selectionRevision=", logonRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairTerminalAsync_posts_only_environment_and_pair_code()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "terminalId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "environment": "Sandbox",
                    "displayName": "Lane 3",
                    "pairingState": "Ready",
                    "isReady": true,
                    "message": "Paired"
                  }
                }
                """);
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PairTerminalAsync(
            CardTerminalEnvironment.Sandbox,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "123456");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(
            "/api/v1/linkly/cloud-backend/terminals/cccccccc-cccc-cccc-cccc-cccccccccccc/pair",
            captured.RequestUri!.AbsolutePath);
        var body = await captured.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["environment", "pairCode"], json.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal("Sandbox", json.RootElement.GetProperty("environment").GetString());
        Assert.Equal("123456", json.RootElement.GetProperty("pairCode").GetString());
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task TestTerminalConnectionAsync_echoes_terminal_version_without_conversion()
    {
        HttpRequestMessage? captured = null;
        const string version = "2026-09-10T01:02:03.1234567";
        var terminalId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = CloneRequestWithBody(request);
            return JsonResponse(JsonSerializer.Serialize(new
            {
                success = true,
                data = new
                {
                    terminalId,
                    environment = "Sandbox",
                    terminalVersion = version,
                    assignedDeviceCode = "POS-1",
                    assignmentRevision = 9,
                    succeeded = true,
                    status = "connected",
                    checkedAt = "2026-09-10T01:03:00Z",
                    message = "Connected"
                }
            }));
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestTerminalConnectionAsync(
            terminalId,
            new LinklyCloudTerminalConnectionTestRequest("Sandbox", version, "POS-1", 9));

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.EndsWith($"/terminals/{terminalId:D}/connection-test", captured.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var body = await captured.Content!.ReadAsStringAsync();
        Assert.Equal(version, ReadJsonString(body, "expectedTerminalVersion"));
        Assert.Equal(version, result.TerminalVersion);
        Assert.Equal("connected", result.Status);
    }

    [Fact]
    public async Task AssignTerminalAsync_sends_null_target_for_unbind_and_returns_authoritative_directory()
    {
        HttpRequestMessage? captured = null;
        var terminalId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = CloneRequestWithBody(request);
            return JsonResponse(
                """
                {"success":true,"data":{"environment":"Sandbox","selectedTerminalId":null,"selectionRevision":4,"terminals":[],"mode":"Active","devices":[]}}
                """);
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.AssignTerminalAsync(
            terminalId,
            new LinklyCloudTerminalAssignmentRequest("Sandbox", "v-1", "POS-1", 3, null, null, 0));

        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.EndsWith($"/terminals/{terminalId:D}/assignment", captured.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var body = await captured.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("targetDeviceCode", out _));
        Assert.False(json.RootElement.TryGetProperty("expectedTargetTerminalId", out _));
        Assert.Equal("v-1", json.RootElement.GetProperty("expectedTerminalVersion").GetString());
        Assert.Equal(4, result.SelectionRevision);
    }

    [Fact]
    public async Task PurchaseAsync_includes_cached_terminal_id_and_selection_revision()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/terminals" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "mode": "Active",
                        "selectedTerminalId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
                        "selectionRevision": 11,
                        "terminals": [{
                          "terminalId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
                          "laneNo": 2,
                          "displayName": "Side Counter",
                          "pairingState": "Ready",
                          "isBusy": false,
                          "isReady": true,
                          "lastHealthStatus": "Healthy",
                          "lastHealthAt": null
                        }]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/health" => ReadyHealthResponse(),
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("terminal-session", "TXN-TERMINAL")),
                "/api/v1/linkly/cloud-backend/transactions/terminal-session/status" => JsonResponse(ApprovedSessionJson("terminal-session", "TXN-TERMINAL")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        await client.GetTerminalsAsync(CardTerminalEnvironment.Sandbox);
        await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        var start = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var body = await start.Content!.ReadAsStringAsync();
        Assert.Equal("dddddddd-dddd-dddd-dddd-dddddddddddd", ReadJsonString(body, "terminalId"));
        Assert.Equal("11", ReadJsonString(body, "selectionRevision"));
        var health = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.Ordinal));
        Assert.Contains("terminalId=dddddddd-dddd-dddd-dddd-dddddddddddd", health.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("selectionRevision=11", health.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_payment_bound_to_terminal_confirmed_before_background_refresh()
    {
        var requests = new List<HttpRequestMessage>();
        var directoryCalls = 0;
        var healthCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/terminals", StringComparison.Ordinal))
            {
                directoryCalls++;
                var terminalId = directoryCalls == 1
                    ? "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
                    : "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
                var revision = directoryCalls == 1 ? 7 : 8;
                return JsonResponse($$"""
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "mode": "Active",
                    "selectedTerminalId": "{{terminalId}}",
                    "selectionRevision": {{revision}},
                    "terminals": []
                  }
                }
                """);
            }

            if (path.EndsWith("/health", StringComparison.Ordinal))
            {
                healthCalls++;
                return healthCalls == 1
                    ? JsonResponse(
                        """
                        { "success": false, "errorCode": "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT", "message": "Selection changed." }
                        """,
                        HttpStatusCode.Conflict)
                    : ReadyHealthResponse();
            }

            return path switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("snapshot-session", "TXN-SNAPSHOT")),
                "/api/v1/linkly/cloud-backend/transactions/snapshot-session/status" => JsonResponse(ApprovedSessionJson("snapshot-session", "TXN-SNAPSHOT")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        await client.GetTerminalsAsync(CardTerminalEnvironment.Sandbox);
        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, directoryCalls);
        var start = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var body = await start.Content!.ReadAsStringAsync();
        Assert.Equal("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", ReadJsonString(body, "terminalId"));
        Assert.Equal("7", ReadJsonString(body, "selectionRevision"));
    }

    [Fact]
    public async Task PurchaseAsync_does_not_adopt_terminal_loaded_after_payment_started()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health", StringComparison.Ordinal) &&
                !request.RequestUri.Query.Contains("terminalId=", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "success": false,
                      "errorCode": "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
                      "message": "Terminal selection is required."
                    }
                    """,
                    HttpStatusCode.Conflict);
            }

            return path switch
            {
                "/api/v1/linkly/cloud-backend/terminals" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "mode": "Active",
                        "selectedTerminalId": "ffffffff-ffff-ffff-ffff-ffffffffffff",
                        "selectionRevision": 21,
                        "terminals": [{
                          "terminalId": "ffffffff-ffff-ffff-ffff-ffffffffffff",
                          "laneNo": 4,
                          "displayName": "Cold Start Counter",
                          "pairingState": "Ready",
                          "isBusy": false,
                          "isReady": true,
                          "lastHealthStatus": "Healthy",
                          "lastHealthAt": null
                        }]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/health" => ReadyHealthResponse(),
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("cold-session", "TXN-COLD")),
                "/api/v1/linkly/cloud-backend/transactions/cold-session/status" => JsonResponse(ApprovedSessionJson("cold-session", "TXN-COLD")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        }, passHealthRequestsToHandler: true);
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var healthRequests = requests
            .Where(request => request.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, healthRequests.Length);
        Assert.DoesNotContain("terminalId=", healthRequests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("terminalId=ffffffff-ffff-ffff-ffff-ffffffffffff", healthRequests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("selectionRevision=21", healthRequests[1].RequestUri!.Query, StringComparison.Ordinal);
        var start = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var body = await start.Content!.ReadAsStringAsync();
        using var transactionJson = JsonDocument.Parse(body);
        Assert.False(transactionJson.RootElement.TryGetProperty("terminalId", out _));
        Assert.False(transactionJson.RootElement.TryGetProperty("selectionRevision", out _));
    }

    [Fact]
    public async Task PurchaseAsync_keeps_legacy_payload_until_multi_terminal_mode_is_active()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/terminals" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "mode": "Draft",
                        "selectedTerminalId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                        "selectionRevision": 4,
                        "terminals": [{
                          "terminalId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                          "laneNo": 3,
                          "displayName": "Draft Counter",
                          "pairingState": "Ready",
                          "isBusy": false,
                          "isReady": true,
                          "lastHealthStatus": "Healthy",
                          "lastHealthAt": null
                        }]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("draft-session", "TXN-DRAFT")),
                "/api/v1/linkly/cloud-backend/transactions/draft-session/status" => JsonResponse(ApprovedSessionJson("draft-session", "TXN-DRAFT")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        await client.GetTerminalsAsync(CardTerminalEnvironment.Sandbox);
        await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        var start = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var body = await start.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.TryGetProperty("terminalId", out _));
        Assert.False(json.RootElement.TryGetProperty("selectionRevision", out _));
    }

    [Fact]
    public async Task PurchaseAsync_selection_conflict_refreshes_directory_without_replaying_payment()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" =>
                    new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": false,
                      "errorCode": "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
                      "message": "Terminal selection changed."
                    }
                    """,
                    HttpStatusCode.Conflict),
                "/api/v1/linkly/cloud-backend/terminals" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "mode": "Active",
                        "selectedTerminalId": "12121212-1212-4212-8212-121212121212",
                        "selectionRevision": 44,
                        "terminals": [{
                          "terminalId": "12121212-1212-4212-8212-121212121212",
                          "laneNo": 1,
                          "displayName": "Updated Counter",
                          "pairingState": "Ready",
                          "isBusy": false,
                          "isReady": true,
                          "lastHealthStatus": "Healthy",
                          "lastHealthAt": null
                        }]
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("linkly.backend.terminalSelectionChanged", result.StatusKey);
        Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions/active", StringComparison.Ordinal));
        Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/terminals", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettlementAsync_returns_not_submitted_when_cancelled_before_any_backend_request()
    {
        var client = CreateClient(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called.")),
            new FakeLinklyTerminalDialogService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await client.SettlementAsync(CreateSession(), CreateSettings(), cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Null(result.SessionId);
        Assert.Equal(ProviderSubmissionState.NotSubmitted, result.ProviderSubmissionState);
    }

    [Fact]
    public async Task PurchaseAsync_uses_localized_backend_message_for_invalid_amount()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var client = CreateClient(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called.")),
            new FakeLinklyTerminalDialogService(),
            localization);

        var result = await client.PurchaseAsync(0m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("刷卡金额必须大于零。", result.Message);
    }

    [Fact]
    public async Task SettlementAsync_starts_one_backend_settlement_and_returns_receipts_without_acknowledging()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/settlements/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/settlements" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "settlement-1",
                        "status": "Completed",
                        "responseCode": "00",
                        "responseText": "SETTLED",
                        "operationType": "Settlement",
                        "operationSuccess": true,
                        "settlementData": "TOTAL=10.00",
                        "settlementReceiptTexts": ["SETTLEMENT", "TOTAL $10.00"],
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.True(result.Succeeded);
        Assert.Equal("settlement-1", result.SessionId);
        Assert.Equal("TOTAL=10.00", result.SettlementData);
        Assert.Equal(["SETTLEMENT", "TOTAL $10.00"], result.ReceiptTexts);
        Assert.Collection(
            requests,
            resumable => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/settlements/resumable?environment=Sandbox", resumable.RequestUri!.AbsoluteUri),
            start => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/settlements", start.RequestUri!.AbsoluteUri));
        var startBody = await requests[1].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(startBody, "environment"));
    }

    [Fact]
    public async Task SettlementAsync_refreshes_active_terminal_scope_when_client_cache_is_cold()
    {
        var requests = new List<HttpRequestMessage>();
        var settlementStarts = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/settlements/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/terminals" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "mode": "Active",
                        "selectedTerminalId": "abababab-abab-abab-abab-abababababab",
                        "selectionRevision": 31,
                        "terminals": [{
                          "terminalId": "abababab-abab-abab-abab-abababababab",
                          "laneNo": 5,
                          "displayName": "Daily Close Counter",
                          "pairingState": "Ready",
                          "isBusy": false,
                          "isReady": true,
                          "lastHealthStatus": "Healthy",
                          "lastHealthAt": null
                        }]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/settlements" when ++settlementStarts == 1 => JsonResponse(
                    """
                    {
                      "success": false,
                      "errorCode": "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
                      "message": "Terminal selection is required."
                    }
                    """,
                    HttpStatusCode.Conflict),
                "/api/v1/linkly/cloud-backend/settlements" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "terminalId": "abababab-abab-abab-abab-abababababab",
                        "terminalDisplayName": "Daily Close Counter",
                        "sessionId": "settlement-cold",
                        "status": "Completed",
                        "responseCode": "00",
                        "responseText": "SETTLED",
                        "operationType": "Settlement",
                        "operationSuccess": true,
                        "settlementData": "TOTAL=20.00",
                        "settlementReceiptTexts": ["SETTLEMENT", "TOTAL $20.00"],
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.True(result.Succeeded);
        Assert.Equal(2, settlementStarts);
        var starts = requests
            .Where(request => request.RequestUri!.AbsolutePath.EndsWith("/settlements", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, starts.Length);
        var firstBody = await starts[0].Content!.ReadAsStringAsync();
        Assert.Null(TryReadJsonString(firstBody, "terminalId"));
        var retryBody = await starts[1].Content!.ReadAsStringAsync();
        Assert.Equal("abababab-abab-abab-abab-abababababab", ReadJsonString(retryBody, "terminalId"));
        Assert.Equal("31", ReadJsonString(retryBody, "selectionRevision"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task SettlementAsync_keeps_unknown_when_backend_rejects_start_without_explicit_not_submitted_code(
        HttpStatusCode statusCode)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath.EndsWith("/resumable", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("settlement request rejected")
                };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.True(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.Unknown, result.ProviderSubmissionState);
        Assert.Collection(
            requests,
            resumable => Assert.EndsWith("/settlements/resumable?environment=Sandbox", resumable.RequestUri!.PathAndQuery, StringComparison.Ordinal),
            start => Assert.EndsWith("/settlements", start.RequestUri!.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettlementAsync_returns_not_submitted_only_for_explicit_backend_start_rejection_code()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/settlements/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/settlements" => JsonResponse(
                """
                { "success": false, "errorCode": "LINKLY_CLOUD_BACKEND_REQUEST_INVALID", "message": "Invalid backend configuration" }
                """,
                HttpStatusCode.BadRequest),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.NotSubmitted, result.ProviderSubmissionState);
        Assert.Contains("Invalid backend configuration", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettlementAsync_names_an_active_card_transaction_when_a_conflict_blocks_new_settlement()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/settlements/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/settlements" => JsonResponse(
                """
                { "success": false, "errorCode": "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION", "message": "Terminal already has an active operation" }
                """,
                HttpStatusCode.Conflict),
            "/api/v1/linkly/cloud-backend/transactions/active" => JsonResponse(PendingSessionJson("active-payment-1", "TXN-PREVIOUS")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal(ProviderSubmissionState.NotSubmitted, result.ProviderSubmissionState);
        Assert.Contains("card transaction", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettlementAsync_waits_for_late_settlement_receipt_before_returning_success()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/settlements/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/settlements" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "settlement-late-receipt",
                        "status": "Completed",
                        "responseCode": "00",
                        "responseText": "SETTLED",
                        "operationType": "Settlement",
                        "operationSuccess": true,
                        "settlementReceiptTexts": [],
                        "notifications": []
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/settlements/settlement-late-receipt/status" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "settlement-late-receipt",
                        "status": "Completed",
                        "responseCode": "00",
                        "responseText": "SETTLED",
                        "operationType": "Settlement",
                        "operationSuccess": true,
                        "settlementReceiptTexts": ["LATE SETTLEMENT RECEIPT"],
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.True(result.Succeeded);
        Assert.Equal(["LATE SETTLEMENT RECEIPT"], result.ReceiptTexts);
        Assert.Collection(
            requests,
            resumable => Assert.EndsWith("/settlements/resumable?environment=Sandbox", resumable.RequestUri!.PathAndQuery, StringComparison.Ordinal),
            start => Assert.EndsWith("/settlements", start.RequestUri!.AbsolutePath, StringComparison.Ordinal),
            status => Assert.EndsWith("/settlements/settlement-late-receipt/status?environment=Sandbox", status.RequestUri!.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettlementAsync_waits_for_late_receipt_after_bank_failure_result()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/settlements/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/settlements" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "settlement-failed-late-receipt",
                        "status": "Completed",
                        "responseCode": "95",
                        "responseText": "SETTLEMENT FAILED",
                        "operationType": "Settlement",
                        "operationSuccess": false,
                        "settlementReceiptTexts": [],
                        "notifications": []
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/settlements/settlement-failed-late-receipt/status" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "settlement-failed-late-receipt",
                        "status": "Completed",
                        "responseCode": "95",
                        "responseText": "SETTLEMENT FAILED",
                        "operationType": "Settlement",
                        "operationSuccess": false,
                        "settlementReceiptTexts": ["FAILED SETTLEMENT RECEIPT"],
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.SettlementAsync(CreateSession(), CreateSettings());

        Assert.False(result.Succeeded);
        Assert.False(result.ResultUnknown);
        Assert.Equal("95", result.ResponseCode);
        Assert.Equal(["FAILED SETTLEMENT RECEIPT"], result.ReceiptTexts);
        Assert.Collection(
            requests,
            resumable => Assert.EndsWith("/settlements/resumable?environment=Sandbox", resumable.RequestUri!.PathAndQuery, StringComparison.Ordinal),
            start => Assert.EndsWith("/settlements", start.RequestUri!.AbsolutePath, StringComparison.Ordinal),
            status => Assert.EndsWith("/settlements/settlement-failed-late-receipt/status?environment=Sandbox", status.RequestUri!.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_rejects_backend_completed_transaction_when_transaction_success_is_false()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-failed-success-session",
                        "status": "Completed",
                        "txnRef": "260601120199",
                        "transactionSuccess": false,
                        "responseCode": "00",
                        "responseText": "SYSTEM ERROR",
                        "displayText": "SYSTEM ERROR",
                        "receiptText": "DECLINED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """));
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("00", result.ResponseCode);
        Assert.Equal("SYSTEM ERROR", result.ResponseText);
        Assert.Equal(0, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_result_unknown_when_backend_approval_lacks_verified_amount()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-approved-without-transaction-success",
                        "status": "Completed",
                        "txnRef": "260601120198",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "displayText": "CARDHOLDER DISPLAY SECRET",
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """));
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.Equal(0, dialog.CloseCallCount);
    }

    [Fact]
    public async Task TestConnectionAsync_uses_backend_logon_test_endpoint_without_local_linkly_token()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "transactionReference": "logon-session-1",
                    "requestedAt": "2026-06-05T04:00:00Z",
                    "httpStatus": 200,
                    "succeeded": true,
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "catid": "12345678",
                    "caid": "123456789012345",
                    "pinPadVersion": "1.8.6.0",
                    "message": "ANZ Linkly Cloud logon succeeded."
                  }
                }
                """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.True(result.Succeeded);
        Assert.Contains("logon", result.Message, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/logon-test?environment=Sandbox",
            request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Content);

        using var requestLog = FindLinklyLog(logs.Lines, "logon-test", "request");
        Assert.Equal("POST", requestLog.RootElement.GetProperty("request").GetProperty("method").GetString());
        using var responseLog = FindLinklyLog(logs.Lines, "logon-test", "response");
        Assert.Equal("00", responseLog.RootElement.GetProperty("response").GetProperty("body").GetProperty("data").GetProperty("responseCode").GetString());
    }

    [Fact]
    public async Task TestConnectionAsync_refreshes_and_retries_safe_logon_with_active_terminal_scope()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/logon-test", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("terminalId=", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "success": false,
                      "errorCode": "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
                      "message": "Terminal selection is required."
                    }
                    """,
                    HttpStatusCode.Conflict);
            }

            return path switch
            {
                "/api/v1/linkly/cloud-backend/terminals" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "mode": "Active",
                        "selectedTerminalId": "34343434-3434-4434-8434-343434343434",
                        "selectionRevision": 52,
                        "terminals": [{
                          "terminalId": "34343434-3434-4434-8434-343434343434",
                          "laneNo": 2,
                          "displayName": "Logon Counter",
                          "pairingState": "Ready",
                          "isBusy": false,
                          "isReady": true,
                          "lastHealthStatus": "Healthy",
                          "lastHealthAt": null
                        }]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/logon-test" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "transactionReference": "logon-terminal-session",
                        "requestedAt": "2026-09-02T00:00:00Z",
                        "httpStatus": 200,
                        "succeeded": true,
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "message": "Logon succeeded."
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.True(result.Succeeded);
        var logonRequests = requests
            .Where(request => request.RequestUri!.AbsolutePath.EndsWith("/logon-test", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, logonRequests.Length);
        Assert.DoesNotContain("terminalId=", logonRequests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("terminalId=34343434-3434-4434-8434-343434343434", logonRequests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("selectionRevision=52", logonRequests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestTransactionStatusAsync_posts_status_test_endpoint_and_logs_request_response()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "transactionReference": "status-session-1",
                    "requestedAt": "2026-06-05T04:00:00Z",
                    "httpStatus": 200,
                    "succeeded": false,
                    "responseCode": "05",
                    "responseText": "DECLINED",
                    "responseTxnRef": "LAST-TXN-1",
                    "responseDate": "050626",
                    "responseTime": "143000",
                    "message": "DECLINED"
                  }
                }
                """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestTransactionStatusAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal("DECLINED", result.Message);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/status-test?environment=Sandbox",
            request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Content);

        using var requestLog = FindLinklyLog(logs.Lines, "transaction-status-test", "request");
        Assert.Equal("POST", requestLog.RootElement.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/status-test?environment=Sandbox", requestLog.RootElement.GetProperty("request").GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, requestLog.RootElement.GetProperty("request").GetProperty("body").ValueKind);

        using var responseLog = FindLinklyLog(logs.Lines, "transaction-status-test", "response");
        Assert.Equal(200, responseLog.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Equal("LAST-TXN-1", responseLog.RootElement.GetProperty("details").GetProperty("txnRef").GetString());
        Assert.Equal("LAST-TXN-1", responseLog.RootElement.GetProperty("details").GetProperty("responseTxnRef").GetString());
        Assert.Equal("DECLINED", responseLog.RootElement.GetProperty("response").GetProperty("body").GetProperty("data").GetProperty("responseText").GetString());
    }

    [Fact]
    public async Task TestConnectionAsync_returns_failed_when_backend_logon_is_declined()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "transactionReference": "logon-session-1",
                "requestedAt": "2026-06-05T04:00:00Z",
                "httpStatus": 200,
                "succeeded": false,
                "responseCode": "99",
                "responseText": "LOGON REQUIRED",
                "catid": null,
                "caid": null,
                "pinPadVersion": null,
                "message": "LOGON REQUIRED"
              }
            }
            """));
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal("LOGON REQUIRED", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_logs_failed_backend_logon_response()
    {
        using var logs = new ConsoleLogCapture();
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "transactionReference": "logon-session-1",
                "requestedAt": "2026-06-05T04:00:00Z",
                "httpStatus": 200,
                "succeeded": false,
                "responseCode": "99",
                "responseText": "LOGON REQUIRED",
                "catid": null,
                "caid": null,
                "pinPadVersion": null,
                "message": "LOGON REQUIRED"
              }
            }
            """));
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Equal("LOGON REQUIRED", result.Message);
        using var responseLog = FindLinklyLog(logs.Lines, "logon-test", "response");
        Assert.Equal("99", responseLog.RootElement.GetProperty("response").GetProperty("body").GetProperty("data").GetProperty("responseCode").GetString());
    }

    [Fact]
    public async Task TestConnectionAsync_does_not_treat_missing_logon_test_endpoint_as_success()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.TestConnectionAsync(CardTerminalEnvironment.Sandbox);

        Assert.False(result.Succeeded);
        Assert.Contains("404", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/logon-test?environment=Sandbox",
            Assert.Single(requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_backend_contract_but_locks_when_completed_status_lacks_transaction_amount()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-1",
                        "status": "Pending",
                        "txnRef": "260601120001",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "okKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"PRESS OK\"], \"OKKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:03Z"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-1",
                        "status": "Completed",
                        "txnRef": "260601120001",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "MERCHANT RECEIPT\nAPPROVED",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        using var activeRequestLog = FindLinklyLog(logs.Lines, "active session", "request");
        Assert.Equal("GET", activeRequestLog.RootElement.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Null, activeRequestLog.RootElement.GetProperty("request").GetProperty("body").ValueKind);
        using var startRequestLog = FindLinklyLog(logs.Lines, "start transaction", "request");
        Assert.Equal("POST", startRequestLog.RootElement.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("Sandbox", startRequestLog.RootElement.GetProperty("request").GetProperty("body").GetProperty("environment").GetString());
        Assert.Equal("P", startRequestLog.RootElement.GetProperty("request").GetProperty("body").GetProperty("txnType").GetString());
        using var startResponseLog = FindLinklyLog(logs.Lines, "start transaction", "response");
        Assert.Equal("260601120001", startResponseLog.RootElement.GetProperty("details").GetProperty("txnRef").GetString());
        Assert.Equal("backend-session-1", startResponseLog.RootElement.GetProperty("response").GetProperty("body").GetProperty("data").GetProperty("sessionId").GetString());
        using var statusResponseLog = FindLinklyLog(logs.Lines, "status", "response");
        Assert.Equal("GET", statusResponseLog.RootElement.GetProperty("response").GetProperty("method").GetString());
        Assert.Equal("Completed", statusResponseLog.RootElement.GetProperty("response").GetProperty("body").GetProperty("data").GetProperty("status").GetString());
        var loggedDisplayText = statusResponseLog.RootElement
            .GetProperty("response")
            .GetProperty("body")
            .GetProperty("data")
            .GetProperty("displayText");
        Assert.True(loggedDisplayText.GetProperty("hasValue").GetBoolean());
        var loggedReceiptText = statusResponseLog.RootElement
            .GetProperty("response")
            .GetProperty("body")
            .GetProperty("data")
            .GetProperty("receiptText");
        Assert.True(loggedReceiptText.GetProperty("hasValue").GetBoolean());
        Assert.Equal(2, loggedReceiptText.GetProperty("lineCount").GetInt32());
        Assert.DoesNotContain("CARDHOLDER DISPLAY SECRET", logs.Lines, StringComparer.Ordinal);
        Assert.DoesNotContain("MERCHANT RECEIPT", logs.Lines, StringComparer.Ordinal);
        Assert.Collection(
            requests,
            active =>
            {
                Assert.Equal(HttpMethod.Get, active.Method);
                Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox", active.RequestUri!.AbsoluteUri);
            },
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions", create.RequestUri!.AbsoluteUri);
            },
            status =>
            {
                Assert.Equal(HttpMethod.Get, status.Method);
                Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/backend-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri);
            });
        var createBody = await requests[1].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(createBody, "environment"));
        Assert.Equal("P", ReadJsonString(createBody, "txnType"));
        Assert.Equal("1000", ReadJsonString(createBody, "amtPurchase"));
        Assert.Null(TryReadJsonString(createBody, "accessToken"));
        Assert.Null(TryReadJsonString(createBody, "restBaseUrl"));
        Assert.Null(TryReadJsonString(createBody, "storeCode"));
        Assert.Null(TryReadJsonString(createBody, "deviceCode"));
        Assert.Null(TryReadJsonString(createBody, "txnRef"));
        Assert.Contains(dialog.States, state => state.DisplayText == "PRESENT CARD");
        Assert.Contains(dialog.States, state => state.ReceiptText == "MERCHANT RECEIPT\nAPPROVED");
        Assert.Equal(0, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_uses_official_get_transaction_payload_refund_reference_in_payment_reference()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(PendingSessionJson("backend-rfn-session", "TXN-RFN")),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-rfn-session",
                        "status": "Completed",
                        "txnRef": "TXN-RFN",
                        "responseCode": "08",
                        "responseText": "APPROVE WITH SIG",
                        "transactionSuccess": true,
                        "displayText": "APPROVE WITH SIG",
                        "receiptText": "MERCHANT RECEIPT\nAPPROVE WITH SIG",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{ \"Response\": { \"Success\": true, \"TxnRef\": \"TXN-RFN\", \"ResponseCode\": \"08\", \"ResponseText\": \"APPROVE WITH SIG\", \"AmtPurchase\": 1008, \"PurchaseAnalysisData\": { \"RFN\": \"RFN-OFFICIAL\" } } }",
                            "receivedAt": "2026-06-01T02:00:03Z"
                          }
                        ]
                      }
                    }
                    """)
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(
            "ANZBACKEND:TXN-RFN:RFN-OFFICIAL:session=backend-rfn-session:environment=Sandbox",
            result.Reference);
        Assert.Equal(10.08m, result.AuthorizedAmount);
        Assert.Equal(10.08m, Assert.Single(result.CardTransactions!).Amount);
    }

    [Fact]
    public async Task PurchaseAsync_returns_result_unknown_when_status_poll_keeps_failing_after_backend_start()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-unknown",
                        "status": "Pending",
                        "txnRef": "260601120099",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new HttpRequestException("backend status offline")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.backend.resultUnknown", result.StatusKey);
        // 预检 + 提交 + 首次状态查询，再加连续 10 次瞬时错误退避重试后才放弃（过去一次失败就放弃，共 3 次请求）。
        Assert.Equal(13, requests.Count);
    }

    [Fact]
    public async Task PurchaseAsync_returns_result_unknown_when_start_response_read_is_cancelled_after_backend_submit()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => throw new OperationCanceledException("start response read cancelled"),
                _ => throw new InvalidOperationException("No further backend calls are expected.")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.backend.resultUnknown", result.StatusKey);
        Assert.DoesNotContain("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        AssertContainsOperationCancelledLog(logs.Lines, transactionSubmitted: true, businessTimeoutCancelled: false);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task PurchaseAsync_logs_result_unknown_warning_with_session_trace_when_status_poll_loses_network()
    {
        var sink = new RecordingApplicationLogSink();
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("net-session", "TXN-NET")),
            "/api/v1/linkly/cloud-backend/transactions/net-session/status" => throw new HttpRequestException("network down"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        PaymentAuthorizationResult result;
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }

        Assert.True(result.ResultUnknown);
        // 交易已提交后通信中断：原先只更新弹窗，中心日志里没有任何记录；现在要有按 sessionId 串联、带异常的 Warning。
        var entry = Assert.Single(
            sink.Entries,
            item => item.Level == "Warning" &&
                item.TraceId == "net-session" &&
                item.Message.Contains("\"operation\":\"transaction\"", StringComparison.Ordinal));
        Assert.Equal("LinklyBackend", entry.Category);
        Assert.Equal(nameof(HttpRequestException), entry.ExceptionType);
        Assert.Contains("\"phase\":\"unknown\"", entry.Message, StringComparison.Ordinal);
        Assert.Contains("\"transactionSubmitted\":true", entry.Message, StringComparison.Ordinal);
        // 发送阶段的网络失败也要单独留下带耗时的记录。
        Assert.Contains(
            sink.Entries,
            item => item.Level == "Warning" &&
                item.Message.Contains("\"operation\":\"status\"", StringComparison.Ordinal) &&
                item.Message.Contains("\"reason\":\"network-error\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_allows_fallback_when_active_session_read_is_cancelled_before_backend_submit()
    {
        using var logs = new ConsoleLogCapture();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => throw new OperationCanceledException("active session read cancelled"),
                _ => throw new InvalidOperationException("No further backend calls are expected.")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.True(result.FallbackAllowed);
        Assert.Equal("linkly.backend.waitCancelled", result.StatusKey);
        Assert.DoesNotContain("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
        AssertContainsOperationCancelledLog(logs.Lines, transactionSubmitted: false, businessTimeoutCancelled: false);
        Assert.Single(requests);
    }

    [Fact]
    public async Task PurchaseAsync_blocks_cloud_backend_when_health_request_is_offline_before_start()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(
            request =>
            {
                requests.Add(CloneRequestWithBody(request));
                if (request.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.Ordinal))
                {
                    throw new HttpRequestException("backend offline");
                }

                throw new InvalidOperationException("Transaction should not start when backend health is offline.");
            },
            passHealthRequestsToHandler: true);
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.True(result.FallbackAllowed);
        Assert.Equal("linkly.backend.unavailable", result.StatusKey);
        Assert.Single(requests);
        Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/health?environment=Sandbox", requests[0].RequestUri!.AbsoluteUri);
        Assert.Empty(dialog.States);
    }

    [Fact]
    public async Task PurchaseAsync_blocks_cloud_backend_when_health_is_not_ready_before_start()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(
            request =>
            {
                requests.Add(CloneRequestWithBody(request));
                return request.RequestUri!.AbsolutePath switch
                {
                    "/api/v1/linkly/cloud-backend/health" => JsonResponse(
                        """
                        {
                          "success": true,
                          "data": {
                            "environment": "Sandbox",
                            "storeCode": "S01",
                            "deviceCode": "TERM-1",
                            "isReady": false,
                            "publicNotificationBaseUrl": null,
                            "checks": [
                              {
                                "code": "PUBLIC_CALLBACK_URL",
                                "isReady": false,
                                "message": "Linkly Cloud notification callback URL must be public HTTPS."
                              }
                            ]
                          }
                        }
                        """),
                    _ => throw new InvalidOperationException("Transaction should not start when backend health is not ready.")
                };
            },
            passHealthRequestsToHandler: true);
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.True(result.FallbackAllowed);
        Assert.Equal("linkly.backend.unavailable", result.StatusKey);
        Assert.Contains("public HTTPS", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(requests);
        Assert.Empty(dialog.States);
    }

    [Fact]
    public async Task PurchaseAsync_allows_fallback_when_backend_rejects_start_before_session()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => JsonResponse(
                    """
                    {
                      "success": false,
                      "message": "Linkly Cloud notification bearer is not configured."
                    }
                    """,
                    HttpStatusCode.BadRequest)
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.FallbackAllowed);
        Assert.False(result.ResultUnknown);
        Assert.Equal("linkly.backend.configIncomplete", result.StatusKey);
        Assert.Contains("notification bearer", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task PurchaseAsync_binds_local_attempt_when_backend_returns_session_before_final_status()
    {
        var bindCount = 0;
        string? boundSessionId = null;
        string? boundTxnRef = null;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-early",
                        "status": "Pending",
                        "txnRef": "260601120099",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-early",
                        "status": "Completed",
                        "txnRef": "260601120099",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog, TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            (sessionId, txnRef, _, _) =>
            {
                bindCount++;
                boundSessionId = sessionId;
                boundTxnRef = txnRef;
                return Task.CompletedTask;
            }));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(1, bindCount);
        Assert.Equal("backend-session-early", boundSessionId);
        Assert.Equal("260601120099", boundTxnRef);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_final_dialog_open_when_backend_declines()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "declined-session-1",
                        "status": "Completed",
                        "txnRef": "260601120012",
                        "responseCode": "05",
                        "responseText": "DECLINED",
                        "displayText": "DECLINED",
                        "receiptText": "DECLINED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal(0, dialog.CloseCallCount);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("DECLINED", finalState.DisplayText);
        Assert.Equal("DECLINED", finalState.ResponseText);
    }

    [Fact]
    public async Task PurchaseAsync_treats_signature_approval_response_code_08_as_approved_and_closes_dialog()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-approved-session-1",
                        "status": "Completed",
                        "txnRef": "260601120014",
                        "responseCode": "08",
                        "responseText": "APPROVE WITH SIG",
                        "transactionSuccess": true,
                        "displayText": "Completed",
                        "receiptText": "TOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("08", result.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", result.ResponseText);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("08", transaction.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", transaction.ResponseText);
        Assert.Contains("PLEASE SIGN:", transaction.ReceiptText, StringComparison.Ordinal);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_posts_auth_sendkey_for_signature_confirmation_before_completing()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-sendkey-session-1",
                        "status": "Pending",
                        "txnRef": "260601120015",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "TOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "authoriseKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"AuthoriseKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-sendkey-session-1",
                        "status": "Pending",
                        "txnRef": "260601120015",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "PROCESSING",
                        "receiptText": "TOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-sendkey-session-1",
                        "status": "Completed",
                        "txnRef": "260601120015",
                        "responseCode": "08",
                        "responseText": "APPROVE WITH SIG",
                        "transactionSuccess": true,
                        "displayText": "Completed",
                        "receiptText": "TOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.Auth, null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("08", result.ResponseCode);
        Assert.Equal("APPROVE WITH SIG", result.ResponseText);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/signature-sendkey-session-1/sendkey", sendKey.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/signature-sendkey-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri));
        var sendKeyBody = await requests[2].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(sendKeyBody, "environment"));
        Assert.Equal(LinklyTerminalDialogKeys.Auth, ReadJsonString(sendKeyBody, "key"));
        Assert.Null(TryReadJsonString(sendKeyBody, "data"));
        var signatureState = Assert.Single(dialog.States.Where(state => state.DisplayText == "SIGNATURE OK?"));
        var button = Assert.Single(signatureState.DisplayButtons!);
        Assert.Equal("linkly.backend.dialog.button.authoriseSignature", button.TextResourceKey);
        Assert.Equal(LinklyTerminalDialogKeys.Auth, button.Key);
        Assert.Contains("PLEASE SIGN:", Assert.Single(result.CardTransactions!).ReceiptText, StringComparison.Ordinal);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_waits_for_final_decline_after_signature_is_rejected()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-receipt-fallback-session-1",
                        "status": "Pending",
                        "txnRef": "260601120016",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "TOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "acceptYesKeyFlag": true,
                        "declineNoKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SWIPE CARD\"],\"CancelKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T01:59:58Z"
                          },
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"AcceptYesKeyFlag\":true,\"DeclineNoKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-receipt-fallback-session-1",
                        "status": "Pending",
                        "txnRef": "260601120016",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "PROCESSING",
                        "receiptText": "TOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": []
                      }
                    }
                    """),
                4 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-receipt-fallback-session-1",
                        "status": "Completed",
                        "txnRef": "260601120016",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "----------------------\n*** MERCHANT COPY ***\nTOTAL      AUD     $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-receipt-fallback-session-1",
                        "status": "Completed",
                        "txnRef": "260601120016",
                        "responseCode": "Q6",
                        "responseText": "SIGNATURE ERROR",
                        "transactionSuccess": false,
                        "displayText": "TRANSACTION DECLINED",
                        "receiptText": "DECLINED - Q6\nSIGNATURE ERROR",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":false,\"TxnRef\":\"260601120016\",\"ResponseCode\":\"Q6\",\"ResponseText\":\"SIGNATURE ERROR\",\"AmtPurchase\":1008}}",
                            "receivedAt": "2026-06-01T02:00:02Z"
                          }
                        ]
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.No, null));
        var signaturePrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, dialog, signaturePrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Q6", result.ResponseCode);
        Assert.Equal("SIGNATURE ERROR", result.ResponseText);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/signature-receipt-fallback-session-1/sendkey", sendKey.RequestUri!.AbsoluteUri),
            completedWithoutResult => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/signature-receipt-fallback-session-1/status?environment=Sandbox", completedWithoutResult.RequestUri!.AbsoluteUri),
            declined => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/signature-receipt-fallback-session-1/status?environment=Sandbox", declined.RequestUri!.AbsoluteUri));
        var sendKeyBody = await requests[2].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(sendKeyBody, "environment"));
        Assert.Equal(LinklyTerminalDialogKeys.No, ReadJsonString(sendKeyBody, "key"));
        Assert.Null(TryReadJsonString(sendKeyBody, "data"));
        Assert.Collection(
            signaturePrinter.Prints,
            signature => Assert.Equal(LinklyBankReceiptKind.SignatureRequired, signature.Kind),
            declined =>
            {
                Assert.Equal(LinklyBankReceiptKind.Declined, declined.Kind);
                Assert.Equal("DECLINED - Q6\nSIGNATURE ERROR", declined.ReceiptText);
            });
        var failedTransaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("Q6", failedTransaction.ResponseCode);
        Assert.Equal("SIGNATURE ERROR", failedTransaction.ResponseText);
        var signatureStates = dialog.States.Where(state => state.DisplayText == "SIGNATURE OK?").ToArray();
        Assert.Collection(
            signatureStates,
            pending =>
            {
                Assert.Equal("Pending", pending.Status);
                Assert.False(pending.IsFinal);
                Assert.False(pending.SupportsCancelPayment);
            },
            completedWithoutResult =>
            {
                Assert.Equal("Completed", completedWithoutResult.Status);
                Assert.False(completedWithoutResult.IsFinal);
                Assert.False(completedWithoutResult.SupportsCancelPayment);
            });
        Assert.Equal(0, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_prints_signature_slip_once_when_signature_prompt_repeats()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 or 3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-print-once-session",
                        "status": "Pending",
                        "txnRef": "260601120017",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "LINE2\nCREDIT ACCOUNT\nPURCHASE AUD $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "acceptYesKeyFlag": true,
                        "declineNoKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"AcceptYesKeyFlag\":true,\"DeclineNoKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-print-once-session",
                        "status": "Completed",
                        "txnRef": "260601120017",
                        "responseCode": "08",
                        "responseText": "APPROVE WITH SIG",
                        "transactionSuccess": true,
                        "displayText": "Completed",
                        "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var signaturePrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, dialog, signaturePrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var print = Assert.Single(signaturePrinter.Prints);
        Assert.Equal("Sandbox", print.Environment);
        Assert.Equal("signature-print-once-session", print.SessionId);
        Assert.Contains("PLEASE SIGN:", print.ReceiptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurchaseAsync_prints_declined_bank_receipt_after_signature_decline_final_result()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-declined-print-session",
                        "status": "Pending",
                        "txnRef": "260601120038",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "LINE2\nCREDIT ACCOUNT\nPURCHASE AUD $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "acceptYesKeyFlag": true,
                        "declineNoKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"AcceptYesKeyFlag\":true,\"DeclineNoKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-declined-print-session",
                        "status": "Pending",
                        "txnRef": "260601120038",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "PROCESSING",
                        "receiptText": "LINE2\nCREDIT ACCOUNT\nPURCHASE AUD $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-declined-print-session",
                        "status": "Completed",
                        "txnRef": "260601120038",
                        "responseCode": "Q6",
                        "responseText": "SIGNATURE ERROR",
                        "transactionSuccess": false,
                        "displayText": "TRANSACTION DECLINED",
                        "receiptText": "DECLINED - Q6\nSIGNATURE ERROR",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":false,\"TxnRef\":\"260601120038\",\"ResponseCode\":\"Q6\",\"ResponseText\":\"SIGNATURE ERROR\",\"AmtPurchase\":1008,\"CardType\":\"VISA\",\"Pan\":\"411111******1234\"}}",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          }
                        ]
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.No, null));
        var bankReceiptPrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, dialog, bankReceiptPrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Q6", result.ResponseCode);
        Assert.Equal("SIGNATURE ERROR", result.ResponseText);
        Assert.Collection(
            bankReceiptPrinter.Prints,
            signature =>
            {
                Assert.Equal(LinklyBankReceiptKind.SignatureRequired, signature.Kind);
                Assert.Contains("PLEASE SIGN:", signature.ReceiptText, StringComparison.Ordinal);
            },
            declined =>
            {
                Assert.Equal(LinklyBankReceiptKind.Declined, declined.Kind);
                Assert.Equal("DECLINED - Q6\nSIGNATURE ERROR", declined.ReceiptText);
                Assert.Equal("VISA", declined.CardType);
                Assert.Equal("****1234", declined.MaskedCardNumber);
                Assert.Equal("Q6", declined.ResponseCode);
                Assert.Equal("SIGNATURE ERROR", declined.ResponseText);
            });
        var sendKeyBody = await requests[2].Content!.ReadAsStringAsync();
        Assert.Equal(LinklyTerminalDialogKeys.No, ReadJsonString(sendKeyBody, "key"));
    }

    [Fact]
    public async Task PurchaseAsync_keeps_declined_when_signature_declined_receipt_print_fails()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "signature-declined-print-fail-session",
                    "status": "Pending",
                    "txnRef": "260601120039",
                    "responseCode": null,
                    "responseText": null,
                    "displayText": "SIGNATURE OK?",
                    "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "declineNoKeyFlag": true,
                    "notifications": [
                      {
                        "type": "display",
                        "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"DeclineNoKeyFlag\":true}}",
                        "receivedAt": "2026-06-01T02:00:00Z"
                      }
                    ]
                  }
                }
                """),
            "/api/v1/linkly/cloud-backend/transactions/signature-declined-print-fail-session/sendkey" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "signature-declined-print-fail-session",
                    "status": "Pending",
                    "txnRef": "260601120039",
                    "displayText": "PROCESSING",
                    "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 202,
                    "notifications": []
                  }
                }
                """),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "signature-declined-print-fail-session",
                    "status": "Completed",
                    "txnRef": "260601120039",
                    "responseCode": "Q6",
                    "responseText": "SIGNATURE ERROR",
                    "transactionSuccess": false,
                    "displayText": "TRANSACTION DECLINED",
                    "receiptText": "DECLINED - Q6\nSIGNATURE ERROR",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.No, null));
        var bankReceiptPrinter = new FakeLinklyBankReceiptPrinter
        {
            Results =
            {
                new ReceiptPrintResult(true, "signature printed"),
                new ReceiptPrintResult(false, "printer offline")
            }
        };
        var client = CreateClient(handler, dialog, bankReceiptPrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Q6", result.ResponseCode);
        Assert.Equal("SIGNATURE ERROR", result.ResponseText);
        Assert.Contains("SIGNATURE ERROR", result.Message, StringComparison.Ordinal);
        Assert.Contains("printer offline", result.Message, StringComparison.Ordinal);
        Assert.Collection(
            bankReceiptPrinter.Prints,
            signature => Assert.Equal(LinklyBankReceiptKind.SignatureRequired, signature.Kind),
            declined => Assert.Equal(LinklyBankReceiptKind.Declined, declined.Kind));
    }

    [Fact]
    public async Task PurchaseAsync_prints_declined_bank_receipt_when_signature_decline_sendkey_network_fails_but_terminal_declines()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-decline-sendkey-network-session",
                        "status": "Pending",
                        "txnRef": "260601120041",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "declineNoKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"DeclineNoKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/transactions/signature-decline-sendkey-network-session/sendkey" => throw new HttpRequestException("connection reset after send"),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-decline-sendkey-network-session",
                        "status": "Completed",
                        "txnRef": "260601120041",
                        "responseCode": "Q6",
                        "responseText": "SIGNATURE ERROR",
                        "transactionSuccess": false,
                        "displayText": "TRANSACTION DECLINED",
                        "receiptText": "DECLINED - Q6\nSIGNATURE ERROR",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":false,\"TxnRef\":\"260601120041\",\"ResponseCode\":\"Q6\",\"ResponseText\":\"SIGNATURE ERROR\",\"AmtPurchase\":1008}}",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          }
                        ]
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.No, null));
        var bankReceiptPrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, dialog, bankReceiptPrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Q6", result.ResponseCode);
        Assert.Equal("SIGNATURE ERROR", result.ResponseText);
        Assert.Contains(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/sendkey", StringComparison.OrdinalIgnoreCase));
        Assert.Collection(
            bankReceiptPrinter.Prints,
            signature => Assert.Equal(LinklyBankReceiptKind.SignatureRequired, signature.Kind),
            declined =>
            {
                Assert.Equal(LinklyBankReceiptKind.Declined, declined.Kind);
                Assert.Equal("DECLINED - Q6\nSIGNATURE ERROR", declined.ReceiptText);
            });
    }

    [Fact]
    public async Task PurchaseAsync_does_not_print_declined_bank_receipt_when_signature_decline_sendkey_is_rejected()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-decline-sendkey-rejected-session",
                        "status": "Pending",
                        "txnRef": "260601120042",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "declineNoKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"DeclineNoKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/transactions/signature-decline-sendkey-rejected-session/sendkey" => JsonResponse(
                    """
                    {
                      "success": false,
                      "data": null,
                      "errorCode": "LINKLY_CLOUD_BACKEND_REQUEST_INVALID",
                      "message": "Linkly Cloud rejected the terminal action."
                    }
                    """,
                    HttpStatusCode.BadRequest),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-decline-sendkey-rejected-session",
                        "status": "Completed",
                        "txnRef": "260601120042",
                        "responseCode": "Q6",
                        "responseText": "SIGNATURE ERROR",
                        "transactionSuccess": false,
                        "displayText": "TRANSACTION DECLINED",
                        "receiptText": "DECLINED - Q6\nSIGNATURE ERROR",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":false,\"TxnRef\":\"260601120042\",\"ResponseCode\":\"Q6\",\"ResponseText\":\"SIGNATURE ERROR\",\"AmtPurchase\":1008}}",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          }
                        ]
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.No, null));
        var bankReceiptPrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, dialog, bankReceiptPrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("Q6", result.ResponseCode);
        Assert.Equal("SIGNATURE ERROR", result.ResponseText);
        Assert.Contains(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/sendkey", StringComparison.OrdinalIgnoreCase));
        var print = Assert.Single(bankReceiptPrinter.Prints);
        Assert.Equal(LinklyBankReceiptKind.SignatureRequired, print.Kind);
        Assert.Contains("PLEASE SIGN:", print.ReceiptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_print_declined_bank_receipt_for_non_signature_decline()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "ordinary-declined-session",
                    "status": "Completed",
                    "txnRef": "260601120040",
                    "responseCode": "55",
                    "responseText": "DECLINED",
                    "transactionSuccess": false,
                    "displayText": "TRANSACTION DECLINED",
                    "receiptText": "DECLINED - 55",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var bankReceiptPrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), bankReceiptPrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Empty(bankReceiptPrinter.Prints);
    }

    [Fact]
    public async Task PurchaseAsync_blocks_signature_approval_until_signature_slip_prints()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-print-retry-session",
                        "status": "Pending",
                        "txnRef": "260601120018",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "SIGNATURE OK?",
                        "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "authoriseKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{\"Response\":{\"DisplayText\":[\"SIGNATURE OK?\"],\"AuthoriseKeyFlag\":true}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-print-retry-session",
                        "status": "Pending",
                        "txnRef": "260601120018",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "PROCESSING",
                        "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-print-retry-session",
                        "status": "Completed",
                        "txnRef": "260601120018",
                        "responseCode": "08",
                        "responseText": "APPROVE WITH SIG",
                        "transactionSuccess": true,
                        "displayText": "Completed",
                        "receiptText": "APPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.Auth, null));
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.Auth, null));
        var signaturePrinter = new FakeLinklyBankReceiptPrinter
        {
            Results =
            {
                new ReceiptPrintResult(false, "printer offline"),
                new ReceiptPrintResult(true, "printed")
            }
        };
        var client = CreateClient(handler, dialog, signaturePrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, signaturePrinter.Prints.Count);
        Assert.Single(requests.Where(request => request.RequestUri!.AbsolutePath.EndsWith("/sendkey", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(dialog.States, state => state.Message == "printer offline");
        Assert.Contains(dialog.States, state => state.DisplayText == "SIGNATURE OK?" && state.Message is null);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Canceled")]
    public async Task PurchaseAsync_treats_backend_cancel_status_as_final_cancel(string status)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    $$"""
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "cancelled-session-1",
                        "status": "{{status}}",
                        "txnRef": "260601120013",
                        "responseCode": "C0",
                        "responseText": "CANCELLED",
                        "displayText": "CANCELLED",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(
            handler,
            dialog,
            TimeSpan.Zero,
            null,
            null,
            businessWait: TimeSpan.FromMilliseconds(20));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, requests.Count);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("CANCELLED", finalState.ResponseText);
    }

    [Fact]
    public async Task RefundAsync_recovers_missing_backend_rfn_from_original_session_notification()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/original-session/status" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "original-session",
                        "status": "Completed",
                        "txnRef": "260601120001",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "MERCHANT RECEIPT\nAPPROVED",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"PurchaseAnalysisData\":{\"RFN\":\"RFN-ORIGINAL\"}}}",
                            "receivedAt": "2026-06-01T02:00:01Z"
                          }
                        ]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "refund-session",
                        "status": "Completed",
                        "txnRef": "260601120099",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "REFUND RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.RefundAsync(
            2m,
            CreateSession(),
            CreateSettings(),
            "ANZBACKEND:260601120001:session=original-session:environment=Sandbox");

        Assert.True(result.Approved);
        var createBody = await requests.Single(request => request.Method == HttpMethod.Post).Content!.ReadAsStringAsync();
        Assert.Contains("RFN-ORIGINAL", createBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefundAsync_recovers_missing_backend_rfn_from_array_purchase_analysis_data()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/original-session/status" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "original-session",
                        "status": "Completed",
                        "txnRef": "260601120001",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "MERCHANT RECEIPT\nAPPROVED",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"PurchaseAnalysisData\":[{\"Key\":\"AMT\",\"Value\":\"200\"},{\"Key\":\"RFN\",\"Value\":\"RFN-ARRAY\"}]}}",
                            "receivedAt": "2026-06-01T02:00:01Z"
                          }
                        ]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "refund-session",
                        "status": "Completed",
                        "txnRef": "260601120099",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "REFUND RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.RefundAsync(
            2m,
            CreateSession(),
            CreateSettings(),
            "ANZBACKEND:260601120001:session=original-session:environment=Sandbox");

        Assert.True(result.Approved);
        var createBody = await requests.Single(request => request.Method == HttpMethod.Post).Content!.ReadAsStringAsync();
        Assert.Contains("RFN-ARRAY", createBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefundAsync_uses_original_backend_txn_ref_when_session_has_no_rfn()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/original-session/status" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "original-session",
                        "status": "Completed",
                        "txnRef": "260601120001",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "MERCHANT RECEIPT\nAPPROVED",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"PurchaseAnalysisData\":{}}}",
                            "receivedAt": "2026-06-01T02:00:01Z"
                          }
                        ]
                      }
                    }
                    """),
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "refund-session",
                        "status": "Completed",
                        "txnRef": "260601120099",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "REFUND RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.RefundAsync(
            2m,
            CreateSession(),
            CreateSettings(),
            "ANZBACKEND:260601120001:session=original-session:environment=Sandbox");

        Assert.True(result.Approved);
        var createBody = await requests.Single(request => request.Method == HttpMethod.Post).Content!.ReadAsStringAsync();
        Assert.Contains("260601120001", createBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurchaseAsync_final_dialog_state_uses_response_text_instead_of_stale_display_prompt()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "final-display-session-1",
                        "status": "Completed",
                        "txnRef": "260601120011",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "PRESENT CARD",
                        "receiptText": "FINAL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var finalState = Assert.Single(dialog.States.Where(state => state.IsFinal));
        Assert.Equal("APPROVED", finalState.DisplayText);
        Assert.Equal("APPROVED", finalState.ResponseText);
        Assert.DoesNotContain(dialog.States, state =>
            state.IsFinal &&
            state.DisplayText == "PRESENT CARD");
    }

    [Fact]
    public async Task PurchaseAsync_waits_briefly_for_receipt_after_transaction_completion()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-2",
                        "status": "Pending",
                        "txnRef": "260601120002",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "okKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"PRESS OK\"], \"OKKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:03Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-2",
                        "status": "Completed",
                        "txnRef": "260601120002",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-session-2",
                        "status": "Completed",
                        "txnRef": "260601120002",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "CUSTOMER RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:01Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(4, requests.Count);
        Assert.Equal("CUSTOMER RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("server-error")]
    [InlineData("invalid-json")]
    [InlineData("local-cancel")]
    public async Task PurchaseAsync_keeps_approved_result_when_receipt_refresh_fails(string failure)
    {
        var dialog = new FakeLinklyTerminalDialogService();
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler((request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(CompletedWithoutReceiptJson("receipt-refresh-approved", "260601120101", approved: true)));
            }

            statusRequests++;
            switch (failure)
            {
                case "network":
                    throw new HttpRequestException("backend offline while fetching receipt");
                case "server-error":
                    return Task.FromResult(JsonResponse(
                        """{ "success": false, "message": "backend unavailable" }""",
                        HttpStatusCode.ServiceUnavailable));
                case "invalid-json":
                    return Task.FromResult(JsonResponse("{ not json"));
                default:
                    // 收银员在补取小票期间点了"停止等待"：终态已确定，不能被本地取消改写成结果未知。
                    dialog.RequestLocalCancel();
                    return new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken);
            }
        });
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("receipt-refresh-approved", result.SessionId);
        Assert.Null(Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Equal(1, statusRequests);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_declined_result_when_receipt_refresh_fails()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return request.Method == HttpMethod.Post
                ? JsonResponse(CompletedWithoutReceiptJson("receipt-refresh-declined", "260601120102", approved: false))
                : throw new HttpRequestException("backend offline while fetching receipt");
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("receipt-refresh-declined", result.SessionId);
        Assert.Equal("05", Assert.Single(result.CardTransactions!).ResponseCode);
    }

    [Theory]
    [InlineData("not-final")]
    [InlineData("other-session")]
    [InlineData("outcome-changed")]
    public async Task PurchaseAsync_does_not_adopt_inconsistent_receipt_refresh(string refresh)
    {
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(CompletedWithoutReceiptJson("receipt-refresh-original", "260601120103", approved: true));
            }

            statusRequests++;
            return JsonResponse(refresh switch
            {
                "not-final" => """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "receipt-refresh-original",
                        "status": "Pending",
                        "txnRef": "260601120103",
                        "recoveryAction": "Retry",
                        "displayText": "PROCESSING",
                        "receiptText": "UNTRUSTED RECEIPT",
                        "recoveryCount": 1,
                        "lastHttpStatus": 503,
                        "notifications": []
                      }
                    }
                    """,
                "other-session" => CompletedWithoutReceiptJson("receipt-refresh-other", "260601120103", approved: true)
                    .Replace("\"receiptText\": null", "\"receiptText\": \"UNTRUSTED RECEIPT\"", StringComparison.Ordinal),
                _ => CompletedWithoutReceiptJson("receipt-refresh-original", "260601120103", approved: false)
                    .Replace("\"receiptText\": null", "\"receiptText\": \"UNTRUSTED RECEIPT\"", StringComparison.Ordinal)
            });
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("receipt-refresh-original", result.SessionId);
        Assert.Null(Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Equal(1, statusRequests);
    }

    [Fact]
    public async Task ResumeSessionUntilFinalAsync_keeps_final_status_when_business_window_expires_during_receipt_wait()
    {
        var handler = new StubHttpMessageHandler((request, cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/transactions/receipt-resume-session/status", StringComparison.Ordinal)
                ? new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken)
                : throw new InvalidOperationException($"Unexpected request {request.RequestUri}"));
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.Zero,
            delayAsync: null,
            localization: null,
            businessWait: TimeSpan.FromMilliseconds(50));

        var status = await client.ResumeSessionUntilFinalAsync(CreateSettings(), CompletedWithoutReceiptStatus("receipt-resume-session"));

        Assert.Equal("receipt-resume-session", status.SessionId);
        Assert.Equal("Completed", status.Status);
        Assert.True(LinklyBackendTerminalClient.IsApprovedFinalTransaction(status));
    }

    [Fact]
    public async Task ResumeSessionUntilFinalAsync_propagates_caller_cancellation_during_receipt_wait()
    {
        using var callerCts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(request =>
            throw new InvalidOperationException($"Unexpected request {request.RequestUri}"));
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromMilliseconds(1),
            delayAsync: (_, cancellationToken) =>
            {
                // 调用方取消（如关闭支付页）仍须原样上抛，不能被"尽力补取小票"吞掉。
                callerCts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ResumeSessionUntilFinalAsync(
                CreateSettings(),
                CompletedWithoutReceiptStatus("receipt-resume-cancelled"),
                callerCts.Token));
    }

    private static string CompletedWithoutReceiptJson(string sessionId, string txnRef, bool approved)
    {
        var responseCode = approved ? "00" : "05";
        var responseText = approved ? "APPROVED" : "DECLINED";
        var transactionSuccess = approved ? "true" : "false";
        return $$"""
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "sessionId": "{{sessionId}}",
                "status": "Completed",
                "txnRef": "{{txnRef}}",
                "responseCode": "{{responseCode}}",
                "responseText": "{{responseText}}",
                "transactionSuccess": {{transactionSuccess}},
                "displayText": "{{responseText}}",
                "receiptText": null,
                "recoveryCount": 0,
                "receiptPrintedAt": null,
                "lastHttpStatus": 200,
                "notifications": []
              }
            }
            """;
    }

    private static LinklyCloudBackendSessionResponse CompletedWithoutReceiptStatus(string sessionId)
    {
        return new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S01",
            "TERM-1",
            sessionId,
            "Completed",
            "TXN-RECEIPT-RESUME",
            ResponseCode: "00",
            ResponseText: "APPROVED",
            RecoveryAction: null,
            DisplayText: "APPROVED",
            CancelKeyFlag: false,
            OKKeyFlag: false,
            AcceptYesKeyFlag: false,
            DeclineNoKeyFlag: false,
            AuthoriseKeyFlag: false,
            InputType: null,
            GraphicCode: null,
            DisplayLines: null,
            ReceiptText: null,
            RecoveryCount: 0,
            ReceiptPrintedAt: null,
            ClientAcknowledgedAt: null,
            LastHttpStatus: 200,
            Notifications: [])
        {
            TransactionSuccess = true
        };
    }

    [Fact]
    public async Task PurchaseAsync_locks_when_completed_terminal_amount_differs()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backend-amount-mismatch",
                        "status": "Completed",
                        "txnRef": "260601120003",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:00Z",
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"TxnRef\":\"260601120003\",\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"AmtPurchase\":999}}"
                          }
                        ]
                      }
                    }
                    """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
    }

    [Fact]
    public async Task PurchaseAsync_uses_protected_session_result_when_later_transaction_notification_conflicts()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "protected-session-1",
                        "status": "Completed",
                        "txnRef": "260601120099",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"AuthCode\":\"AUTH1\",\"AmtPurchase\":1000}}",
                            "receivedAt": "2026-06-01T02:00:00Z"
                          },
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":false,\"ResponseCode\":\"05\",\"ResponseText\":\"DECLINED\",\"AuthCode\":\"BAD\"}}",
                            "receivedAt": "2026-06-01T02:00:01Z"
                          }
                        ]
                      }
                    }
                    """);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var transaction = Assert.Single(result.CardTransactions!);
        Assert.Equal("00", transaction.ResponseCode);
        Assert.Equal("APPROVED", transaction.ResponseText);
        Assert.Equal("AUTH1", transaction.AuthCode);
    }

    [Fact]
    public async Task PurchaseAsync_rejects_existing_active_session_before_starting_new_transaction()
    {
        var requests = new List<HttpRequestMessage>();
        var bindCount = 0;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "active-session-1",
                        "status": "Pending",
                        "txnRef": "260601120010",
                        "displayText": "REMOVE CARD",
                        "receiptText": null,
                        "recoveryCount": 1,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 409,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog, TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            (_, _, _, _) =>
            {
                bindCount++;
                return Task.CompletedTask;
            }));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Contains("unfinished card transaction", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, bindCount);
        Assert.DoesNotContain(requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var activeRequest = Assert.Single(requests);
        Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox", activeRequest.RequestUri!.AbsoluteUri);
        var finalState = Assert.Single(dialog.States);
        Assert.Equal("active-session-1", finalState.SessionId);
        Assert.True(finalState.IsFinal);
        Assert.Contains("unfinished card transaction", finalState.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_start_new_transaction_after_preflight_takeover_succeeds()
    {
        var requests = new List<HttpRequestMessage>();
        var takeoverCount = 0;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return JsonResponse(PendingSessionJson("preflight-active-1", "TXN-PREVIOUS"));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            (_, _, _, _) => Task.CompletedTask,
            TakeOverActiveSessionAsync: (_, _, _) =>
            {
                takeoverCount++;
                return Task.FromResult(LinklyActiveSessionTakeoverResult.Success);
            }));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("linkly.backend.activeSessionRequiresRecovery", result.StatusKey);
        Assert.Equal(1, takeoverCount);
        Assert.DoesNotContain(requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_rejects_active_session_after_conflict_without_generic_backend_failure()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": false,
                      "errorCode": "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION",
                      "message": "Active session exists."
                    }
                    """,
                    HttpStatusCode.Conflict),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "conflict-session-1",
                        "status": "Pending",
                        "txnRef": "260601120020",
                        "displayText": "WAITING",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 409,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.NotEqual("ANZ Linkly Cloud backend communication failed.", result.Message);
        Assert.Contains("unfinished card transaction", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            requests,
            activeBeforeStart => Assert.Equal(HttpMethod.Get, activeBeforeStart.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            activeAfterConflict => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/active?environment=Sandbox", activeAfterConflict.RequestUri!.AbsoluteUri));
        var finalState = Assert.Single(dialog.States);
        Assert.Equal("conflict-session-1", finalState.SessionId);
        Assert.True(finalState.IsFinal);
        Assert.Contains("unfinished card transaction", finalState.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_sends_attempt_identity_so_backend_derives_the_txn_ref_already_persisted_locally()
    {
        var attemptGuid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        const string attemptTxnRef = "PMND6WYVF2GBVY6G";
        var requests = new List<HttpRequestMessage>();
        string? boundTxnRef = null;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("recoverable-session", attemptTxnRef)),
                "/api/v1/linkly/cloud-backend/transactions/recoverable-session/status" => JsonResponse(ApprovedSessionJson("recoverable-session", attemptTxnRef)),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            attemptGuid,
            (_, txnRef, _, _) =>
            {
                boundTxnRef = txnRef;
                return Task.CompletedTask;
            },
            TxnRef: LinklyLocalTxnRef.Create('P', attemptGuid.ToString("D"))));

        await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        // 交易引用仍由后端生成，客户端只交出 attempt 身份；后端用同一算法派生出的引用必须等于本地已落库的值，
        // 请求发出后若断电，本地记录才能靠它认领会话。请求体里不得出现 txnRef（契约守卫测试禁止客户端指定）。
        var start = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var body = await start.Content!.ReadAsStringAsync();
        Assert.Equal(attemptGuid.ToString("D"), ReadJsonString(body, "attemptGuid"));
        Assert.Null(TryReadJsonString(body, "txnRef"));
        Assert.Equal(attemptTxnRef, LinklyLocalTxnRef.Create('P', attemptGuid.ToString("D")));
        Assert.Equal(attemptTxnRef, boundTxnRef);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("P0123456789ABCDE")]
    public async Task PurchaseAsync_leaves_txn_ref_to_backend_when_local_reference_is_not_derived_from_the_attempt(string? attemptTxnRef)
    {
        var requests = new List<HttpRequestMessage>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("backend-ref-session", "260601120020ABCD")),
                "/api/v1/linkly/cloud-backend/transactions/backend-ref-session/status" => JsonResponse(ApprovedSessionJson("backend-ref-session", "260601120020ABCD")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            (_, _, _, _) => Task.CompletedTask,
            TxnRef: attemptTxnRef));

        await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        // 本地引用不是由该 attempt 身份派生时，后端派生出的值会与本地记录对不上；这时宁可让后端随机生成。
        var start = Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Null(TryReadJsonString(await start.Content!.ReadAsStringAsync(), "attemptGuid"));
    }

    [Fact]
    public async Task PurchaseAsync_takes_over_finished_but_unacknowledged_session_when_conflict_has_no_active_session()
    {
        var requests = new List<HttpRequestMessage>();
        LinklyCloudBackendSessionResponse? takenOver = null;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                // 上一笔在绑定会话前断电，顾客随后在终端完成了刷卡：会话已到终态（不再 active），但 POS 从未确认。
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": false,
                      "errorCode": "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION",
                      "message": "Active session exists."
                    }
                    """,
                    HttpStatusCode.Conflict),
                "/api/v1/linkly/cloud-backend/transactions/resumable" => JsonResponse(ApprovedSessionJson("orphan-session", "P0123456789ABCDE")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            (_, _, _, _) => Task.CompletedTask,
            TxnRef: "PZZZZZZZZZZZZZZZ",
            TakeOverActiveSessionAsync: (_, status, _) =>
            {
                takenOver = status;
                return Task.FromResult(LinklyActiveSessionTakeoverResult.Success);
            }));

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        // 过去这里只会提示"稍后再试"，而后端闸门会一直拒绝，这台设备的 Cloud 刷卡只能靠改库解除。
        Assert.Equal("orphan-session", takenOver?.SessionId);
        Assert.Equal("P0123456789ABCDE", takenOver?.TxnRef);
        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("linkly.backend.activeSessionRequiresRecovery", result.StatusKey);
        Assert.Single(requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_unknown_conflict_does_not_take_over_or_replay_transaction()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    { "success": false, "message": "Conflict without an error code." }
                    """,
                    HttpStatusCode.Conflict),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("linkly.backend.terminalSelectionChanged", result.StatusKey);
        Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Single(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/transactions/active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_continues_polling_when_sendkey_returns_completed_without_transaction_result()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "completed-race-session",
                        "status": "Pending",
                        "txnRef": "260601120031",
                        "displayText": "PRESS OK",
                        "okKeyFlag": true,
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{ \"Response\": { \"Success\": true, \"TxnRef\": \"260601120198\", \"ResponseCode\": \"00\", \"ResponseText\": \"APPROVED\" } }"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "completed-race-session",
                        "status": "Completed",
                        "txnRef": "260601120031",
                        "responseCode": null,
                        "responseText": null,
                        "transactionSuccess": null,
                        "displayText": "APPROVED",
                        "okKeyFlag": true,
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{ \"Response\": { \"Success\": true, \"TxnRef\": \"260601120198\", \"ResponseCode\": \"00\", \"ResponseText\": \"APPROVED\" } }"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "completed-race-session",
                        "status": "Completed",
                        "txnRef": "260601120031",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "APPROVED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:04Z",
                        "lastHttpStatus": 200,
                        "notifications": [
                          {
                            "type": "transaction",
                            "payloadJson": "{\"Response\":{\"Success\":true,\"ResponseCode\":\"00\",\"ResponseText\":\"APPROVED\",\"AuthCode\":\"AUTH1\",\"TxnRef\":\"260601120031\",\"AmtPurchase\":1000}}",
                            "receivedAt": "2026-06-01T02:00:03Z"
                          }
                        ]
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction("OK", null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("00", result.ResponseCode);
        Assert.Equal("APPROVED", result.ResponseText);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/completed-race-session/sendkey", sendKey.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/completed-race-session/status?environment=Sandbox", status.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task PurchaseAsync_sends_dialog_key_during_status_polling()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-session-1",
                        "status": "Pending",
                        "txnRef": "260601120030",
                        "displayText": "PRESS OK",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-session-1",
                        "status": "Pending",
                        "txnRef": "260601120030",
                        "displayText": "PROCESSING",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-session-1",
                        "status": "Completed",
                        "txnRef": "260601120030",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "KEY RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:04Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction("OK", null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-session-1/sendkey", sendKey.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri));
        var sendKeyBody = await requests[2].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(sendKeyBody, "environment"));
        Assert.Equal("0", ReadJsonString(sendKeyBody, "key"));
        Assert.Null(TryReadJsonString(sendKeyBody, "data"));
        Assert.Null(TryReadJsonString(sendKeyBody, "accessToken"));
        Assert.Contains(dialog.States, state => state.DisplayText == "PRESS OK");
    }

    [Fact]
    public async Task PurchaseAsync_real_backend_dialog_cancel_payment_posts_cancel_sendkey()
    {
        var requests = new List<HttpRequestMessage>();
        var dialog = new WpfLinklyTerminalDialogService(new LocalizationService());
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "real-dialog-cancel-session",
                        "status": "Pending",
                        "txnRef": "260601120031",
                        "displayText": "SWIPE CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "cancelKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"SWIPE CARD\"], \"CancelKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:03Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => StatusAfterCashierClicksCancel(dialog),
                4 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "real-dialog-cancel-session",
                        "status": "Completed",
                        "txnRef": "260601120031",
                        "responseCode": "TM",
                        "responseText": "OPERATOR TIMEOUT",
                        "displayText": "CANCELLED",
                        "receiptText": "CANCEL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = new LinklyBackendTerminalClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            dialog,
            TimeSpan.Zero,
            delayAsync: null,
            localization: null,
            paymentAttemptContextAccessor: null,
            businessWait: TimeSpan.FromSeconds(5));

        var result = await client.PurchaseAsync(8m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Equal("linkly.backend.cancelled", result.StatusKey);
        Assert.Equal("ANZ Linkly Cloud transaction was cancelled.", result.Message);
        Assert.Equal("TM", result.ResponseCode);
        Assert.Equal("OPERATOR TIMEOUT", result.ResponseText);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/real-dialog-cancel-session/status?environment=Sandbox", status.RequestUri!.AbsoluteUri),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/real-dialog-cancel-session/sendkey", sendKey.RequestUri!.AbsoluteUri));
        var sendKeyBody = await requests[3].Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(sendKeyBody, "environment"));
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, ReadJsonString(sendKeyBody, "key"));
        Assert.Null(TryReadJsonString(sendKeyBody, "data"));
        Assert.False(dialog.LocalCancelToken.IsCancellationRequested);

        static HttpResponseMessage StatusAfterCashierClicksCancel(WpfLinklyTerminalDialogService dialog)
        {
            Assert.True(dialog.IsCancelPaymentVisible);
            // 这里模拟收银员在 SWIPE CARD 等待页点击取消；真实 dialog 必须发 Linkly Key=0，而不是本地停止等待。
            dialog.CancelPaymentCommand.Execute(null);
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "real-dialog-cancel-session",
                    "status": "Pending",
                    "txnRef": "260601120031",
                    "displayText": "SWIPE CARD",
                    "receiptText": null,
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "cancelKeyFlag": true,
                    "notifications": [
                      {
                        "type": "display",
                        "payloadJson": "{ \"Response\": { \"DisplayText\": [\"SWIPE CARD\"], \"CancelKeyFlag\": \"1\" } }",
                        "receivedAt": "2026-06-01T02:00:04Z"
                      }
                    ]
                  }
                }
                """);
        }
    }

    [Fact]
    public async Task PurchaseAsync_returns_cancelled_unknown_when_backend_async_dialog_requests_local_cancel()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "local-cancel-session",
                        "status": "Pending",
                        "txnRef": "260601120130",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 500,
                        "recoveryAction": "Retry",
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException("Local cancel should stop polling before the next status request.")
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.LocalCancel, null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.backend.cancelledUnknown", result.StatusKey);
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_stops_waiting_when_local_cancel_is_requested_during_poll_delay()
    {
        var requests = new List<HttpRequestMessage>();
        var dialog = new FakeLinklyTerminalDialogService();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "delay-cancel-session",
                        "status": "Pending",
                        "txnRef": "260601120132",
                        "displayText": "PRESENT CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 500,
                        "recoveryAction": "Retry",
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException("Local cancel during delay should stop before the next status request.")
            };
        });
        var client = CreateClient(
            handler,
            dialog,
            TimeSpan.FromMilliseconds(10),
            (_, token) =>
            {
                dialog.RequestLocalCancel();
                return Task.Delay(TimeSpan.FromMilliseconds(10), token);
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.backend.cancelledUnknown", result.StatusKey);
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_closes_dialog_with_uncancelled_token_when_caller_token_is_cancelled_after_final_status()
    {
        using var callerCts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "final-close-session",
                    "status": "Completed",
                    "txnRef": "260601120133",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "FINAL RECEIPT",
                    "recoveryCount": 0,
                    "receiptPrintedAt": "2026-06-01T02:00:04Z",
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """),
            _ => throw new InvalidOperationException("Final completed transaction should not poll again.")
        });
        var dialog = new FakeLinklyTerminalDialogService
        {
            OnUpdate = _ => callerCts.Cancel()
        };
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings(), callerCts.Token);

        Assert.True(result.Approved);
        Assert.Equal(1, dialog.CloseCallCount);
        var closeTokenWasCancelled = Assert.Single(dialog.CloseTokenCancellationStates);
        Assert.False(closeTokenWasCancelled);
    }

    [Fact]
    public async Task PurchaseAsync_completes_when_pending_status_has_official_success_evidence()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions/resumable" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "pending-approved-session",
                        "status": "Pending",
                        "txnRef": "260601120134",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "PENDING SUCCESS RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException("Official success evidence should complete without another status poll.")
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("PENDING SUCCESS RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Equal(1, dialog.CloseCallCount);
        Assert.DoesNotContain(requests, request =>
            request.RequestUri!.AbsolutePath.Contains("/pending-approved-session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_continues_polling_when_cancel_sendkey_is_rejected_after_submission()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "cancel-key-session",
                        "status": "Pending",
                        "txnRef": "260601120131",
                        "displayText": "PRESS CANCEL",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "cancelKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"PRESS CANCEL\"], \"CancelKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": false,
                      "data": null,
                      "errorCode": "LINKLY_CLOUD_BACKEND_REQUEST_INVALID",
                      "message": "Linkly Cloud rejected the terminal action. Continue waiting for the transaction result."
                    }
                    """,
                    HttpStatusCode.BadRequest),
                4 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "cancel-key-session",
                        "status": "Completed",
                        "txnRef": "260601120131",
                        "responseCode": "TM",
                        "responseText": "OPERATOR TIMEOUT",
                        "displayText": "TRANSACTION DECLINED",
                        "receiptText": "CANCEL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:04Z",
                        "lastHttpStatus": 400,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException("Cancel sendkey rejection should poll the final session result once.")
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.False(result.FallbackAllowed);
        Assert.Equal("linkly.backend.cancelled", result.StatusKey);
        Assert.Equal("ANZ Linkly Cloud transaction was cancelled.", result.Message);
        Assert.Equal("TM", result.ResponseCode);
        Assert.Equal("OPERATOR TIMEOUT", result.ResponseText);
        Assert.Equal(4, requests.Count);
        Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/cancel-key-session/sendkey", requests[2].RequestUri!.AbsoluteUri);
        Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/cancel-key-session/status?environment=Sandbox", requests[3].RequestUri!.AbsoluteUri);
        Assert.Equal(1, dialog.CloseCallCount);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_show_sendkey_failed_message_when_cancel_sendkey_is_rejected_but_session_is_still_pending()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "cancel-key-pending-session",
                        "status": "Pending",
                        "txnRef": "260601120132",
                        "displayText": "SWIPE CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "cancelKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"SWIPE CARD\"], \"CancelKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": false,
                      "data": null,
                      "errorCode": "LINKLY_CLOUD_BACKEND_REQUEST_INVALID",
                      "message": "Linkly Cloud rejected the terminal action."
                    }
                    """,
                    HttpStatusCode.BadRequest),
                4 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "cancel-key-pending-session",
                        "status": "Pending",
                        "txnRef": "260601120132",
                        "displayText": "SWIPE CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "cancelKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"SWIPE CARD\"], \"CancelKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:05Z"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "cancel-key-pending-session",
                        "status": "Cancelled",
                        "txnRef": "260601120132",
                        "responseCode": "C0",
                        "responseText": "CANCELLED",
                        "displayText": "CANCELLED",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.OkCancel, null));
        var client = CreateClient(handler, dialog, TimeSpan.Zero, null);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal("CANCELLED", result.ResponseText);
        Assert.DoesNotContain(dialog.States, state =>
            state.SessionId == "cancel-key-pending-session" &&
            state.DisplayText == "SWIPE CARD" &&
            state.Message == "Card terminal action failed. Try again or recover the transaction.");
    }

    [Fact]
    public async Task PurchaseAsync_initial_pending_without_display_notification_shows_no_terminal_actions()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "initial-pending-session",
                    "status": "Pending",
                    "txnRef": "260601120030",
                    "displayText": "TAP OK TO CONTINUE",
                    "receiptText": null,
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 202,
                    "okKeyFlag": true,
                    "notifications": []
                  }
                }
                """),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "initial-pending-session",
                    "status": "Completed",
                    "txnRef": "260601120030",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "INITIAL PENDING RECEIPT",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var pending = Assert.Single(dialog.States.Where(state => state.SessionId == "initial-pending-session" && state.Status == "Pending"));
        Assert.Empty(pending.DisplayButtons!);
    }

    [Fact]
    public async Task PurchaseAsync_display_notification_without_key_flags_shows_no_terminal_actions()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "display-no-key-session",
                    "status": "Pending",
                    "txnRef": "260601120033",
                    "displayText": "TAP OK TO CONTINUE",
                    "receiptText": null,
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 202,
                    "notifications": [
                      {
                        "type": "display",
                        "payloadJson": "{ \"Response\": { \"DisplayText\": [\"TAP OK TO CONTINUE\"] } }",
                        "receivedAt": "2026-06-01T02:00:05Z"
                      }
                    ]
                  }
                }
                """),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "display-no-key-session",
                    "status": "Completed",
                    "txnRef": "260601120033",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "NO KEY RECEIPT",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var pending = Assert.Single(dialog.States.Where(state => state.SessionId == "display-no-key-session" && state.Status == "Pending"));
        Assert.Equal("Waiting for card terminal result...", pending.DisplayText);
        Assert.Empty(pending.DisplayButtons!);
    }

    [Fact]
    public async Task PurchaseAsync_display_notification_without_key_flags_uses_localized_waiting_result_message()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "display-no-key-session",
                    "status": "Pending",
                    "txnRef": "260601120033",
                    "displayText": "TAP OK TO CONTINUE",
                    "receiptText": null,
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 202,
                    "notifications": [
                      {
                        "type": "display",
                        "payloadJson": "{ \"Response\": { \"DisplayText\": [\"TAP OK TO CONTINUE\"] } }",
                        "receivedAt": "2026-06-01T02:00:05Z"
                      }
                    ]
                  }
                }
                """),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "display-no-key-session",
                    "status": "Completed",
                    "txnRef": "260601120033",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "NO KEY RECEIPT",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog, localization);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var pending = Assert.Single(dialog.States.Where(state => state.SessionId == "display-no-key-session" && state.Status == "Pending"));
        Assert.Equal("等待刷卡终端返回结果...", pending.DisplayText);
        Assert.Empty(pending.DisplayButtons!);
    }

    [Theory]
    [InlineData("SWIPE CARD", "cancelKeyFlag")]
    [InlineData("PRESENT CARD", "cancelKeyFlag")]
    [InlineData("TAP CARD", "okKeyFlag")]
    [InlineData("INSERT CARD", "cancelKeyFlag")]
    public async Task PurchaseAsync_card_terminal_wait_display_suppresses_sendkey_buttons(
        string displayText,
        string enabledFlag)
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                $$"""
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "card-wait-session",
                    "status": "Pending",
                    "txnRef": "260601120035",
                    "displayText": "{{displayText}}",
                    "receiptText": null,
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 202,
                    "{{enabledFlag}}": true,
                    "notifications": [
                      {
                        "type": "display",
                        "payloadJson": "{ \"Response\": { \"DisplayText\": [\"{{displayText}}\"], \"{{enabledFlag}}\": \"1\" } }",
                        "receivedAt": "2026-06-01T02:00:05Z"
                      }
                    ]
                  }
                }
                """),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "card-wait-session",
                    "status": "Completed",
                    "txnRef": "260601120035",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "CARD WAIT RECEIPT",
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var pending = Assert.Single(dialog.States.Where(state => state.SessionId == "card-wait-session" && state.Status == "Pending"));
        Assert.Equal(displayText, pending.DisplayText);
        Assert.Empty(pending.DisplayButtons!);
    }

    [Fact]
    public async Task PurchaseAsync_tap_ok_prompt_is_masked_without_sending_key_and_refreshes_status_immediately()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "tap-ok-session",
                        "status": "Pending",
                        "txnRef": "260601120036",
                        "displayText": "TAP OK TO CONTINUE",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "okKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"TAP OK TO CONTINUE\"], \"OKKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:05Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "tap-ok-session",
                        "status": "Pending",
                        "txnRef": "260601120036",
                        "displayText": "SWIPE CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"SWIPE CARD\"], \"CancelKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:06Z"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "tap-ok-session",
                        "status": "Completed",
                        "txnRef": "260601120036",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "AUTO OK RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            firstStatus => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/tap-ok-session/status?environment=Sandbox", firstStatus.RequestUri!.AbsoluteUri),
            finalStatus => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/tap-ok-session/status?environment=Sandbox", finalStatus.RequestUri!.AbsoluteUri));
        Assert.DoesNotContain(requests, request => request.RequestUri!.AbsolutePath.EndsWith("/sendkey", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dialog.States, state => state.DisplayText == "TAP OK TO CONTINUE");
        Assert.Contains(dialog.States, state =>
            state.SessionId == "tap-ok-session" &&
            state.Status == "Pending" &&
            state.DisplayText == "Waiting for card terminal result..." &&
            state.DisplayButtons!.Count == 0 &&
            !state.SupportsCancelPayment);
        Assert.Contains(dialog.States, state =>
            state.SessionId == "tap-ok-session" &&
            state.Status == "Pending" &&
            state.DisplayText == "SWIPE CARD" &&
            state.DisplayButtons!.Count == 0 &&
            state.SupportsCancelPayment);
    }

    [Fact]
    public async Task PurchaseAsync_recovers_signature_buttons_from_latest_display_notification_when_status_flags_are_stale()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-stale-flags-session",
                        "status": "Pending",
                        "txnRef": "260601120037",
                        "responseCode": null,
                        "responseText": null,
                        "displayText": "PROCESSING\r\nPLEASE WAIT",
                        "receiptText": "LINE2\nCREDIT ACCOUNT\nPURCHASE AUD $10.08\nAPPROVE WITH SIG - 08\nPLEASE SIGN:",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:06Z",
                        "lastHttpStatus": 202,
                        "cancelKeyFlag": true,
                        "okKeyFlag": false,
                        "acceptYesKeyFlag": false,
                        "declineNoKeyFlag": false,
                        "authoriseKeyFlag": false,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"PROCESSING\", \"PLEASE WAIT\"], \"CancelKeyFlag\": false, \"AcceptYesKeyFlag\": false, \"DeclineNoKeyFlag\": false, \"AuthoriseKeyFlag\": false, \"OKKeyFlag\": false } }",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          },
                          {
                            "type": "receipt",
                            "payloadJson": "{ \"Response\": { \"ReceiptText\": [\"APPROVE WITH SIG - 08\", \"PLEASE SIGN:\"] } }",
                            "receivedAt": "2026-06-01T02:00:05Z"
                          },
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"SIGNATURE OK?\"], \"CancelKeyFlag\": false, \"AcceptYesKeyFlag\": true, \"DeclineNoKeyFlag\": true, \"AuthoriseKeyFlag\": false, \"OKKeyFlag\": false } }",
                            "receivedAt": "2026-06-01T02:00:06Z"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "signature-stale-flags-session",
                        "status": "Completed",
                        "txnRef": "260601120037",
                        "responseCode": "Q6",
                        "responseText": "SIGNATURE ERROR",
                        "transactionSuccess": false,
                        "displayText": "TRANSACTION DECLINED",
                        "receiptText": "DECLINED - Q6\nSIGNATURE ERROR",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:06Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction(LinklyTerminalDialogKeys.No, null));
        var signaturePrinter = new FakeLinklyBankReceiptPrinter();
        var client = CreateClient(handler, dialog, signaturePrinter);

        var result = await client.PurchaseAsync(10.08m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        var signatureState = Assert.Single(dialog.States.Where(state => state.SessionId == "signature-stale-flags-session" && state.Status == "Pending"));
        Assert.Equal("PROCESSING\r\nPLEASE WAIT", signatureState.DisplayText);
        Assert.Collection(
            signatureState.DisplayButtons!,
            yes =>
            {
                Assert.Equal("linkly.backend.dialog.button.yesApproved", yes.TextResourceKey);
                Assert.Equal(LinklyTerminalDialogKeys.Yes, yes.Key);
            },
            no =>
            {
                Assert.Equal("linkly.backend.dialog.button.noDeclined", no.TextResourceKey);
                Assert.Equal(LinklyTerminalDialogKeys.No, no.Key);
            });
        Assert.False(signatureState.SupportsCancelPayment);
        var sendKeyBody = await requests[2].Content!.ReadAsStringAsync();
        Assert.Equal(LinklyTerminalDialogKeys.No, ReadJsonString(sendKeyBody, "key"));
        var declinedPrint = Assert.Single(signaturePrinter.Prints);
        Assert.Equal(LinklyBankReceiptKind.Declined, declinedPrint.Kind);
        Assert.Equal("DECLINED - Q6\nSIGNATURE ERROR", declinedPrint.ReceiptText);
    }

    [Fact]
    public async Task PurchaseAsync_keeps_polling_current_session_when_sendkey_request_fails()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-failure-session-1",
                        "status": "Pending",
                        "txnRef": "260601120031",
                        "displayText": "PRESS OK",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "okKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"PRESS OK\"], \"OKKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:04Z"
                          }
                        ]
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": false,
                      "message": "Send key failed."
                    }
                    """,
                    HttpStatusCode.InternalServerError),
                4 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-failure-session-1",
                        "status": "Pending",
                        "txnRef": "260601120031",
                        "displayText": "WAITING FOR CARD",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "key-failure-session-1",
                        "status": "Completed",
                        "txnRef": "260601120031",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "RECOVERED RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:04Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        dialog.EnqueueAction(new LinklyTerminalDialogAction("OK", null));
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method),
            sendKey => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-failure-session-1/sendkey", sendKey.RequestUri!.AbsoluteUri),
            status => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-failure-session-1/status?environment=Sandbox", status.RequestUri!.AbsoluteUri),
            finalStatus => Assert.Equal("https://api.example/api/v1/linkly/cloud-backend/transactions/key-failure-session-1/status?environment=Sandbox", finalStatus.RequestUri!.AbsoluteUri));

        // sendkey 失败后应继续展示当前会话，并提示用户重试或恢复交易。
        Assert.Contains(dialog.States, state =>
            state.SessionId == "key-failure-session-1" &&
            state.Message == "Card terminal action failed. Try again or recover the transaction." &&
            state.DisplayText == "WAITING FOR CARD" &&
            state.DisplayButtons is { Count: 0 });
        Assert.Contains(dialog.States, state =>
            state.SessionId == "key-failure-session-1" &&
            state.DisplayText == "WAITING FOR CARD");
        Assert.Equal("RECOVERED RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
    }

    [Fact]
    public async Task PurchaseAsync_builds_single_ok_cancel_display_button_when_ok_and_cancel_flags_are_both_true()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "ok-cancel-session-1",
                        "status": "Pending",
                        "txnRef": "260601120032",
                        "displayText": "CHOOSE ACTION",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "okKeyFlag": true,
                        "cancelKeyFlag": true,
                        "notifications": [
                          {
                            "type": "display",
                            "payloadJson": "{ \"Response\": { \"DisplayText\": [\"CHOOSE ACTION\"], \"OKKeyFlag\": \"1\", \"CancelKeyFlag\": \"1\" } }",
                            "receivedAt": "2026-06-01T02:00:05Z"
                          }
                        ]
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "ok-cancel-session-1",
                        "status": "Completed",
                        "txnRef": "260601120032",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "OK CANCEL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": "2026-06-01T02:00:05Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var dialogState = Assert.Single(dialog.States.Where(state => state.Status == "Pending"));
        var button = Assert.Single(dialogState.DisplayButtons!);
        Assert.Equal("linkly.backend.dialog.button.okCancel", button.TextResourceKey);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, button.Key);
    }

    [Fact]
    public async Task PurchaseAsync_builds_cancel_button_only_when_cancel_flag_is_true()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "cancel-only-session-1",
                    "status": "Pending",
                    "txnRef": "260601120034",
                    "displayText": "CANCEL AVAILABLE",
                    "receiptText": null,
                    "recoveryCount": 0,
                    "receiptPrintedAt": null,
                    "lastHttpStatus": 200,
                    "cancelKeyFlag": true,
                    "notifications": [
                      {
                        "type": "display",
                        "payloadJson": "{ \"Response\": { \"DisplayText\": [\"CANCEL AVAILABLE\"], \"CancelKeyFlag\": \"1\" } }",
                        "receivedAt": "2026-06-01T02:00:06Z"
                      }
                    ]
                  }
                }
                """),
            _ => JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "cancel-only-session-1",
                    "status": "Completed",
                    "txnRef": "260601120034",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "CANCEL ONLY RECEIPT",
                    "recoveryCount": 0,
                    "receiptPrintedAt": "2026-06-01T02:00:06Z",
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """)
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        var dialogState = Assert.Single(dialog.States.Where(state => state.SessionId == "cancel-only-session-1" && state.Status == "Pending"));
        var button = Assert.Single(dialogState.DisplayButtons!);
        Assert.Equal("linkly.backend.dialog.button.cancel", button.TextResourceKey);
        Assert.Equal(LinklyTerminalDialogKeys.OkCancel, button.Key);
        Assert.True(button.IsDestructive);
    }

    [Fact]
    public async Task PurchaseAsync_refreshes_202_pending_status_immediately()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "short-poll-session",
                        "status": "Pending",
                        "txnRef": "260601120040",
                        "recoveryAction": "Retry",
                        "displayText": "PROCESSING",
                        "receiptText": null,
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 202,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "short-poll-session",
                        "status": "Completed",
                        "txnRef": "260601120040",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "SHORT POLL RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var delays = new List<TimeSpan>();
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromMilliseconds(100),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Empty(delays);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/transactions/short-poll-session/status?environment=Sandbox",
            requests[2].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PurchaseAsync_uses_exponential_backoff_for_408_and_5xx_recovery()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backoff-session",
                        "status": "Pending",
                        "txnRef": "260601120050",
                        "recoveryAction": "Retry",
                        "displayText": "RECOVERING",
                        "receiptText": null,
                        "recoveryCount": 1,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 408,
                        "notifications": []
                      }
                    }
                    """),
                3 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backoff-session",
                        "status": "Pending",
                        "txnRef": "260601120050",
                        "recoveryAction": "Retry",
                        "displayText": "RECOVERING",
                        "receiptText": null,
                        "recoveryCount": 2,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 500,
                        "notifications": []
                      }
                    }
                    """),
                _ => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "backoff-session",
                        "status": "Completed",
                        "txnRef": "260601120050",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "BACKOFF RECEIPT",
                        "recoveryCount": 2,
                        "receiptPrintedAt": "2026-06-01T02:00:05Z",
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """)
            };
        });
        var delays = new List<TimeSpan>();
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromMilliseconds(100),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Collection(
            delays,
            first =>
            {
                Assert.InRange(first.TotalMilliseconds, 100, 300);
                Assert.NotEqual(TimeSpan.FromMilliseconds(200), first);
            },
            second =>
            {
                Assert.InRange(second.TotalMilliseconds, 200, 600);
                Assert.NotEqual(TimeSpan.FromMilliseconds(400), second);
            });
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/transactions/backoff-session/recover",
            requests[2].RequestUri!.AbsoluteUri);
        Assert.Equal(
            "https://api.example/api/v1/linkly/cloud-backend/transactions/backoff-session/recover",
            requests[3].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_mark_receipt_printed_before_print_service_confirms()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => JsonResponse(
                    """
                    {
                      "success": true,
                      "data": {
                        "environment": "Sandbox",
                        "storeCode": "S01",
                        "deviceCode": "TERM-1",
                        "sessionId": "mark-receipt-session",
                        "status": "Completed",
                        "txnRef": "260601120060",
                        "responseCode": "00",
                        "responseText": "APPROVED",
                        "transactionSuccess": true,
                        "displayText": "APPROVED",
                        "receiptText": "MARK RECEIPT",
                        "recoveryCount": 0,
                        "receiptPrintedAt": null,
                        "lastHttpStatus": 200,
                        "notifications": []
                      }
                    }
                    """),
                _ => throw new InvalidOperationException("PurchaseAsync must not call receipt/printed before the receipt is actually printed.")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal("MARK RECEIPT", Assert.Single(result.CardTransactions!).ReceiptText);
        Assert.Collection(
            requests,
            active => Assert.Equal(HttpMethod.Get, active.Method),
            start => Assert.Equal(HttpMethod.Post, start.Method));
        Assert.DoesNotContain(requests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/receipt/printed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurchaseAsync_rejects_printed_active_session_without_returning_receipt_text()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "sessionId": "printed-session",
                    "status": "Completed",
                    "txnRef": "260601120070",
                    "responseCode": "00",
                    "responseText": "APPROVED",
                    "transactionSuccess": true,
                    "displayText": "APPROVED",
                    "receiptText": "ALREADY PRINTED RECEIPT",
                    "recoveryCount": 1,
                    "receiptPrintedAt": "2026-06-01T02:00:07Z",
                    "lastHttpStatus": 200,
                    "notifications": []
                  }
                }
                """);
        });
        var dialog = new FakeLinklyTerminalDialogService();
        var client = CreateClient(handler, dialog);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.Null(result.CardTransactions);
        Assert.Single(requests);
        var finalState = Assert.Single(dialog.States);
        Assert.Equal("printed-session", finalState.SessionId);
        Assert.True(finalState.IsFinal);
    }

    [Fact]
    public async Task PurchaseAsync_does_not_use_short_configured_timeout_before_linkly_business_wait()
    {
        var requests = new List<string>();
        var statusWait = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusToken = CancellationToken.None;
        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
            {
                requests.Add($"{request.Method} {request.RequestUri!.AbsoluteUri}");
                if (request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (request.RequestUri.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(PendingSessionJson("short-timeout-session", "TXN-SHORT")));
                }

                if (request.RequestUri.AbsolutePath.Contains("/transactions/short-timeout-session", StringComparison.Ordinal))
                {
                    statusToken = cancellationToken;
                    statusStarted.TrySetResult();
                    return statusWait.Task.WaitAsync(cancellationToken);
                }

                throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
            });
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.Zero,
            null,
            null,
            timeProvider: timeProvider);
        var settings = CreateSettings() with { TerminalTimeout = TimeSpan.FromMilliseconds(30) };

        using var cancellation = new CancellationTokenSource();
        var purchaseTask = client.PurchaseAsync(10m, CreateSession(), settings, cancellation.Token);
        try
        {
            // 必须进入交易查询后再推进时间，预检耗时不能替代业务等待的验证。
            await statusStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            // 中文注释：业务等待计时器在 FakeTimeProvider 上，Advance 会在本线程同步触发到期回调并级联取消链接令牌，
            // 因此推进后立即读取查询令牌即可确定结论，不需要墙钟等待。推进到业务等待前一刻，远超 30ms 的终端超时设置。
            timeProvider.Advance(LinklyTimeoutPolicy.BusinessWait - TimeSpan.FromMilliseconds(1));
            if (statusToken.IsCancellationRequested || purchaseTask.IsCompleted)
            {
                Assert.Fail($"Purchase stopped waiting before business wait. statusCancelled={statusToken.IsCancellationRequested} completed={purchaseTask.IsCompleted} requests={string.Join(" | ", requests)}");
            }

            statusWait.SetResult(JsonResponse(ApprovedSessionJson("short-timeout-session", "TXN-SHORT")));
            var result = await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.True(result.Approved);
        }
        finally
        {
            // 失败路径也取消并观察交易任务，避免影响同进程其他测试。
            cancellation.Cancel();
            await purchaseTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        }
    }

    [Fact]
    public async Task PurchaseAsync_starts_transaction_business_wait_after_preflight()
    {
        var tokens = new Dictionary<string, CancellationToken>(StringComparer.Ordinal);
        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/active", StringComparison.Ordinal))
                {
                    tokens["active"] = cancellationToken;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                if (path.EndsWith("/transactions", StringComparison.Ordinal))
                {
                    tokens["start"] = cancellationToken;
                    return Task.FromResult(JsonResponse(PendingSessionJson("fresh-business-wait-session", "TXN-FRESH")));
                }

                if (path.Contains("/transactions/fresh-business-wait-session", StringComparison.Ordinal))
                {
                    tokens["status"] = cancellationToken;
                    return Task.FromResult(JsonResponse(ApprovedSessionJson("fresh-business-wait-session", "TXN-FRESH")));
                }

                throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
            },
            (request, cancellationToken) =>
            {
                tokens["health"] = cancellationToken;
                return ReadyHealthResponse();
            });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.NotEqual(tokens["health"], tokens["start"]);
        Assert.NotEqual(tokens["active"], tokens["start"]);
        Assert.NotEqual(tokens["health"], tokens["status"]);
        Assert.NotEqual(tokens["active"], tokens["status"]);
    }

    [Fact]
    public async Task ResumeSessionUntilFinalAsync_times_out_with_result_unknown_details()
    {
        var recoverWait = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/transactions/recovery-timeout-session/recover", StringComparison.Ordinal))
            {
                return recoverWait.Task.WaitAsync(cancellationToken);
            }

            throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
        });
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.Zero,
            delayAsync: null,
            localization: null,
            businessWait: TimeSpan.FromMilliseconds(20));
        var activeStatus = new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S01",
            "TERM-1",
            "recovery-timeout-session",
            "Pending",
            "TXN-RECOVERY",
            ResponseCode: null,
            ResponseText: null,
            RecoveryAction: "Retry",
            DisplayText: "PRESENT CARD",
            CancelKeyFlag: false,
            OKKeyFlag: false,
            AcceptYesKeyFlag: false,
            DeclineNoKeyFlag: false,
            AuthoriseKeyFlag: false,
            InputType: null,
            GraphicCode: null,
            DisplayLines: null,
            ReceiptText: null,
            RecoveryCount: 0,
            ReceiptPrintedAt: null,
            ClientAcknowledgedAt: null,
            LastHttpStatus: 202,
            Notifications: []);

        var exception = await Assert.ThrowsAsync<LinklyBackendResultUnknownException>(
            () => client.ResumeSessionUntilFinalAsync(CreateSettings(), activeStatus));

        Assert.Contains("recovery timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SessionId=recovery-timeout-session", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TxnRef=TXN-RECOVERY", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Status=Pending", exception.Message, StringComparison.Ordinal);
        Assert.Contains("result is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgeSession_sends_supervisor_resolution_flag_only_for_supervisor_decisions(bool supervisorResolved)
    {
        string? path = null;
        string? body = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(PendingSessionJson("ack-flag-session", "TXN-ACK-FLAG"));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        if (supervisorResolved)
        {
            await client.AcknowledgeSupervisorResolvedSessionAsync(CreateSettings(), "ack-flag-session");
        }
        else
        {
            await client.AcknowledgeSessionAsync(CreateSettings(), "ack-flag-session");
        }

        Assert.Equal("/api/v1/linkly/cloud-backend/transactions/ack-flag-session/acknowledge", path);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("Sandbox", document.RootElement.GetProperty("environment").GetString());
        Assert.Equal(supervisorResolved, document.RootElement.GetProperty("supervisorResolved").GetBoolean());
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog)
    {
        return CreateClient(handler, dialog, TimeSpan.Zero, null, null);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        ILinklyBankReceiptPrinter bankReceiptPrinter)
    {
        return CreateClient(handler, dialog, TimeSpan.Zero, null, null, bankReceiptPrinter: bankReceiptPrinter);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        ILocalizationService localization)
    {
        return CreateClient(handler, dialog, TimeSpan.Zero, null, localization);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        return CreateClient(handler, dialog, pollInterval, delayAsync, null);
    }

    private static LinklyBackendTerminalClient CreateClient(
        StubHttpMessageHandler handler,
        FakeLinklyTerminalDialogService dialog,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        ILocalizationService? localization,
        ILinklyPaymentAttemptContextAccessor? paymentAttemptContextAccessor = null,
        TimeSpan? businessWait = null,
        ILinklyBankReceiptPrinter? bankReceiptPrinter = null,
        TimeProvider? timeProvider = null)
    {
        return new LinklyBackendTerminalClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            dialog,
            pollInterval,
            delayAsync,
            localization,
            paymentAttemptContextAccessor,
            businessWait,
            bankReceiptPrinter,
            timeProvider);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            "HB POS",
            "S01",
            "Main",
            "TERM-1",
            "C001",
            "Cashier",
            true,
            0);
    }

    private static CardTerminalSettings CreateSettings()
    {
        return CardTerminalSettings.FromEnvironment() with
        {
            Processor = CardProcessorKind.Linkly,
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
            TerminalTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // 大多数交互测试关注对话框、轮询和按键；旧桩只有 APPROVED 状态而没有终端金额。
    // 仅在当前桩已收到本次付款请求时补齐真实 transaction 通知，避免跨测试共享金额状态。
    private static string AddVerifiedAmountToGenericApprovedFixture(string json, string? requestedMinorAmount)
    {
        if (string.IsNullOrWhiteSpace(requestedMinorAmount) ||
            json.Contains("backend-approved-without-transaction-success", StringComparison.Ordinal) ||
            json.Contains("backend-session-1", StringComparison.Ordinal) ||
            !json.Contains("\"notifications\": []", StringComparison.Ordinal) ||
            (!json.Contains("\"responseCode\": \"00\"", StringComparison.Ordinal) &&
             !json.Contains("\"responseCode\": \"08\"", StringComparison.Ordinal)))
        {
            return json;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("txnRef", out var txnRef) ||
                string.IsNullOrWhiteSpace(txnRef.GetString()))
            {
                return json;
            }

            var responseCode = data.TryGetProperty("responseCode", out var code) ? code.GetString() : "00";
            var responseText = data.TryGetProperty("responseText", out var text) ? text.GetString() : "APPROVED";
            var payloadJson = JsonSerializer.Serialize(new
            {
                Response = new
                {
                    Success = true,
                    TxnRef = txnRef.GetString(),
                    ResponseCode = responseCode,
                    ResponseText = responseText,
                    AmtPurchase = long.Parse(requestedMinorAmount, System.Globalization.CultureInfo.InvariantCulture)
                }
            });
            var notification = JsonSerializer.Serialize(new { type = "transaction", payloadJson });
            return json.Replace("\"notifications\": []", $"\"notifications\": [{notification}]", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static HttpResponseMessage ReadyHealthResponse()
    {
        return JsonResponse(
            """
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "isReady": true,
                "publicNotificationBaseUrl": "https://pos.example/linkly/",
                "checks": [
                  {
                    "code": "STORE_CREDENTIAL",
                    "isReady": true,
                    "message": "Linkly Cloud store credential is configured."
                  }
                ]
              }
            }
            """);
    }

    [Fact]
    public async Task RefundAsync_sends_attempt_identity_so_backend_derives_the_refund_txn_ref_already_persisted_locally()
    {
        // 退款 POST 已受理而响应丢失时，订单会话必须能按 R 引用关联到本地退款 attempt。
        var attemptGuid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        const string refundTxnRef = "RB7VFXNSJNPBZ6QX";
        var requests = new List<HttpRequestMessage>();
        string? boundTxnRef = null;
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("refund-derived-session", refundTxnRef)),
                "/api/v1/linkly/cloud-backend/transactions/refund-derived-session/status" => JsonResponse(ApprovedSessionJson("refund-derived-session", refundTxnRef)),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            attemptGuid,
            (_, txnRef, _, _) =>
            {
                boundTxnRef = txnRef;
                return Task.CompletedTask;
            },
            TxnRef: LinklyLocalTxnRef.Create('R', attemptGuid.ToString("D"))));

        await client.RefundAsync(
            2m,
            CreateSession(),
            CreateSettings(),
            LinklyBackendPaymentReference.Format("260601120001", "original-session", "Sandbox", "RFN-ORIGINAL"));

        var start = Assert.Single(requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        var body = await start.Content!.ReadAsStringAsync();
        Assert.Equal("R", ReadJsonString(body, "txnType"));
        Assert.Equal(attemptGuid.ToString("D"), ReadJsonString(body, "attemptGuid"));
        Assert.Null(TryReadJsonString(body, "txnRef"));
        // 与服务端测试里同一已知向量对齐，钉住两端 R 派生算法不漂移。
        Assert.Equal(refundTxnRef, LinklyLocalTxnRef.Create('R', attemptGuid.ToString("D")));
        Assert.Equal(refundTxnRef, boundTxnRef);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("P0123456789ABCDE")]
    public async Task RefundAsync_leaves_txn_ref_to_backend_when_local_reference_is_not_the_refund_reference_derived_from_the_attempt(string? attemptTxnRef)
    {
        // 升级前遗留的 32 位随机退款引用，以及销售的 P 引用，都不是该 attempt 的 R 派生值：不发送 AttemptGuid，
        // 否则后端派生出的引用会与本地记录对不上。
        var requests = new List<HttpRequestMessage>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("refund-legacy-session", "260601120020ABCD")),
                "/api/v1/linkly/cloud-backend/transactions/refund-legacy-session/status" => JsonResponse(ApprovedSessionJson("refund-legacy-session", "260601120020ABCD")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            (_, _, _, _) => Task.CompletedTask,
            TxnRef: attemptTxnRef));

        await client.RefundAsync(
            2m,
            CreateSession(),
            CreateSettings(),
            LinklyBackendPaymentReference.Format("260601120001", "original-session", "Sandbox", "RFN-ORIGINAL"));

        var start = Assert.Single(requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Null(TryReadJsonString(await start.Content!.ReadAsStringAsync(), "attemptGuid"));
    }

    [Fact]
    public async Task PurchaseAsync_does_not_send_attempt_identity_when_the_local_reference_is_the_refund_reference()
    {
        // 销售请求只认 P 派生值：即便本地引用恰好是该 attempt 的 R 派生值也不能当作销售引用。
        var attemptGuid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var requests = new List<HttpRequestMessage>();
        var accessor = new LinklyPaymentAttemptContextAccessor();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(CloneRequestWithBody(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/linkly/cloud-backend/transactions/active" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/v1/linkly/cloud-backend/transactions" => JsonResponse(PendingSessionJson("p-vs-r-session", "260601120020ABCD")),
                "/api/v1/linkly/cloud-backend/transactions/p-vs-r-session/status" => JsonResponse(ApprovedSessionJson("p-vs-r-session", "260601120020ABCD")),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService(), TimeSpan.Zero, null, null, accessor);
        using var scope = accessor.Begin(new LinklyPaymentAttemptContext(
            attemptGuid,
            (_, _, _, _) => Task.CompletedTask,
            TxnRef: LinklyLocalTxnRef.Create('R', attemptGuid.ToString("D"))));

        await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        var start = Assert.Single(requests, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Null(TryReadJsonString(await start.Content!.ReadAsStringAsync(), "attemptGuid"));
    }

    [Fact]
    public async Task PurchaseAsync_retries_transient_status_failures_with_exponential_backoff_until_the_terminal_result_arrives()
    {
        // 一次 502 / 503 就把付款页锁成“结果未知”是错的：终端上的交易还在继续，可能随后批准。
        var statusRequests = 0;
        var delays = new List<TimeSpan>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(PendingSessionJson("transient-session", "TXN-TRANSIENT"));
            }

            statusRequests++;
            return statusRequests switch
            {
                1 => throw new LinklyBackendHttpException("bad gateway", HttpStatusCode.BadGateway),
                2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                3 => JsonResponse(PendingSessionJson("transient-session", "TXN-TRANSIENT")),
                _ => JsonResponse(ApprovedSessionJson("transient-session", "TXN-TRANSIENT"))
            };
        });
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromSeconds(1),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.False(result.ResultUnknown);
        Assert.Equal(4, statusRequests);
        // 第 1 次失败后 ≈1s、第 2 次失败后 ≈2s（±25% 抖动），随后恢复为 1s 的正常轮询间隔。
        Assert.Equal(3, delays.Count);
        Assert.InRange(delays[0].TotalMilliseconds, 750, 1250);
        Assert.InRange(delays[1].TotalMilliseconds, 1500, 2500);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task PurchaseAsync_retries_status_http_errors_that_the_backend_treats_as_recoverable(HttpStatusCode failure)
    {
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(PendingSessionJson("recoverable-http-session", "TXN-RH"));
            }

            return ++statusRequests == 1
                ? new HttpResponseMessage(failure)
                : JsonResponse(ApprovedSessionJson("recoverable-http-session", "TXN-RH"));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, statusRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task PurchaseAsync_does_not_retry_deterministic_status_errors(HttpStatusCode failure)
    {
        // 400 是我方请求格式错误，重试没有意义；401/403/404/409 同样不是瞬时错误。
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(PendingSessionJson("deterministic-session", "TXN-DET"));
            }

            statusRequests++;
            return new HttpResponseMessage(failure);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.ResultUnknown);
        Assert.Equal(1, statusRequests);
    }

    [Fact]
    public async Task PurchaseAsync_retries_status_requests_cancelled_by_an_endpoint_switch_while_the_business_window_is_open()
    {
        // API 端点切换窗口内请求被统一取消（取消令牌既不是调用方的也不是业务窗口的）：属于瞬时错误。
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(PendingSessionJson("switch-session", "TXN-SWITCH")));
            }

            return ++statusRequests == 1
                ? Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true))
                : Task.FromResult(JsonResponse(ApprovedSessionJson("switch-session", "TXN-SWITCH")));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, statusRequests);
    }

    [Fact]
    public async Task PurchaseAsync_retries_the_recover_request_when_it_fails_transiently()
    {
        var recoverRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(RecoveringSessionJson("recover-retry-session", "TXN-RR"));
            }

            Assert.EndsWith("/recover", path, StringComparison.Ordinal);
            return ++recoverRequests == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : JsonResponse(ApprovedSessionJson("recover-retry-session", "TXN-RR"));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(2, recoverRequests);
    }

    [Fact]
    public async Task PurchaseAsync_gives_up_after_consecutive_transient_failures_and_reports_result_unknown()
    {
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(PendingSessionJson("exhausted-session", "TXN-EX"));
            }

            statusRequests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.False(result.Approved);
        Assert.True(result.ResultUnknown);
        // 首次请求 + 连续 10 次退避重试，之后放弃。
        Assert.Equal(11, statusRequests);
    }

    [Fact]
    public async Task PurchaseAsync_a_successful_poll_resets_the_transient_failure_budget()
    {
        // 间歇性故障（每隔一次成功）不能累计到阈值：成功一次即清零。
        var statusRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(PendingSessionJson("flaky-session", "TXN-FLAKY"));
            }

            statusRequests++;
            if (statusRequests > 40)
            {
                return JsonResponse(ApprovedSessionJson("flaky-session", "TXN-FLAKY"));
            }

            return statusRequests % 2 == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : JsonResponse(PendingSessionJson("flaky-session", "TXN-FLAKY"));
        });
        var client = CreateClient(handler, new FakeLinklyTerminalDialogService());

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.Approved);
        Assert.Equal(41, statusRequests);
    }

    [Fact]
    public async Task PurchaseAsync_stops_retrying_transient_failures_when_the_business_window_expires_on_the_time_provider()
    {
        var statusRequests = 0;
        var timeProvider = new FakeTimeProvider();
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return JsonResponse(PendingSessionJson("window-session", "TXN-WIN"));
            }

            statusRequests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromSeconds(1),
            (delay, cancellationToken) =>
            {
                // 退避等待走虚拟时间：推进后业务窗口到期并级联取消链接令牌。
                timeProvider.Advance(delay);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            localization: null,
            businessWait: TimeSpan.FromSeconds(5),
            timeProvider: timeProvider);

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings());

        Assert.True(result.ResultUnknown);
        // 1s + 2s 之后第三次退避（≈4s）越过 5s 窗口：远少于 10 次重试上限，说明受业务窗口约束。
        Assert.InRange(statusRequests, 3, 4);
    }

    [Fact]
    public async Task PurchaseAsync_propagates_caller_cancellation_during_transient_backoff()
    {
        using var callerCts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/active", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return path.EndsWith("/transactions", StringComparison.Ordinal)
                ? JsonResponse(PendingSessionJson("caller-cancel-session", "TXN-CC"))
                : new HttpResponseMessage(HttpStatusCode.BadGateway);
        });
        var client = CreateClient(
            handler,
            new FakeLinklyTerminalDialogService(),
            TimeSpan.FromSeconds(1),
            (_, cancellationToken) =>
            {
                // 调用方取消（关闭支付页、退出程序）必须原样上抛，不能被“瞬时错误重试”吞掉。
                callerCts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var result = await client.PurchaseAsync(10m, CreateSession(), CreateSettings(), callerCts.Token);

        Assert.False(result.Approved);
        Assert.True(callerCts.IsCancellationRequested);
        Assert.True(result.ResultUnknown);
    }

    private static string RecoveringSessionJson(string sessionId, string txnRef)
    {
        return $$"""
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "sessionId": "{{sessionId}}",
                "status": "Pending",
                "txnRef": "{{txnRef}}",
                "recoveryAction": "Retry",
                "displayText": "RECOVERING",
                "receiptText": null,
                "recoveryCount": 1,
                "receiptPrintedAt": null,
                "lastHttpStatus": 408,
                "notifications": []
              }
            }
            """;
    }

    private static string PendingSessionJson(string sessionId, string txnRef)
    {
        return $$"""
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "sessionId": "{{sessionId}}",
                "status": "Pending",
                "txnRef": "{{txnRef}}",
                "responseCode": null,
                "responseText": null,
                "displayText": "Processing",
                "recoveryCount": 0,
                "lastHttpStatus": 202,
                "notifications": []
              }
            }
            """;
    }

    private static string ApprovedSessionJson(string sessionId, string txnRef)
    {
        return $$"""
            {
              "success": true,
              "data": {
                "environment": "Sandbox",
                "storeCode": "S01",
                "deviceCode": "TERM-1",
                "sessionId": "{{sessionId}}",
                "status": "Completed",
                "txnRef": "{{txnRef}}",
                "responseCode": "00",
                "responseText": "APPROVED",
                "transactionSuccess": true,
                "displayText": "APPROVED",
                "receiptText": "APPROVED RECEIPT",
                "recoveryCount": 0,
                "lastHttpStatus": 200,
                "notifications": []
              }
            }
            """;
    }

    private static HttpRequestMessage CloneRequestWithBody(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
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
        return TryReadJsonString(json, propertyName)
            ?? throw new InvalidOperationException($"Missing JSON property {propertyName}.");
    }

    private static string? TryReadJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? _healthHandler;
        private readonly bool _passHealthRequestsToHandler;
        private string? _requestedMinorAmount;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler,
            bool passHealthRequestsToHandler = false)
            : this((request, _) => Task.FromResult(handler(request)), passHealthRequestsToHandler)
        {
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            bool passHealthRequestsToHandler = false)
        {
            _handler = handler;
            _passHealthRequestsToHandler = passHealthRequestsToHandler;
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> healthHandler)
        {
            _handler = handler;
            _healthHandler = healthHandler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/api/v1/linkly/cloud-backend/transactions", StringComparison.Ordinal) &&
                request.Content is not null)
            {
                _requestedMinorAmount = TryReadJsonString(
                    request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult(),
                    "amtPurchase");
            }

            HttpResponseMessage response;
            if (!_passHealthRequestsToHandler &&
                request.RequestUri!.AbsolutePath.EndsWith("/api/v1/linkly/cloud-backend/health", StringComparison.Ordinal))
            {
                response = _healthHandler?.Invoke(request, cancellationToken) ?? ReadyHealthResponse();
            }
            else
            {
                response = await _handler(request, cancellationToken);
            }

            if (response.Content is null || string.IsNullOrWhiteSpace(_requestedMinorAmount))
            {
                return response;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var updatedJson = AddVerifiedAmountToGenericApprovedFixture(json, _requestedMinorAmount);
            if (string.Equals(json, updatedJson, StringComparison.Ordinal))
            {
                return response;
            }

            response.Content.Dispose();
            response.Content = new StringContent(updatedJson, Encoding.UTF8, "application/json");
            return response;
        }
    }

    private sealed class FakeLinklyTerminalDialogService : ILinklyTerminalDialogService
    {
        private readonly Queue<LinklyTerminalDialogAction?> _actions = new();
        private readonly CancellationTokenSource _localCancelCts = new();

        public List<LinklyTerminalDialogState> States { get; } = [];

        public List<bool> CloseTokenCancellationStates { get; } = [];

        public int CloseCallCount { get; private set; }

        public Action<LinklyTerminalDialogState>? OnUpdate { get; set; }

        public CancellationToken LocalCancelToken => _localCancelCts.Token;

        public void EnqueueAction(LinklyTerminalDialogAction? action)
        {
            _actions.Enqueue(action);
        }

        public void RequestLocalCancel()
        {
            _localCancelCts.Cancel();
        }

        public Task<LinklyTerminalDialogAction?> UpdateAsync(
            LinklyTerminalDialogState state,
            CancellationToken cancellationToken)
        {
            States.Add(state);
            OnUpdate?.Invoke(state);
            return Task.FromResult(_actions.Count == 0 ? null : _actions.Dequeue());
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCallCount++;
            CloseTokenCancellationStates.Add(cancellationToken.IsCancellationRequested);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLinklyBankReceiptPrinter : ILinklyBankReceiptPrinter
    {
        public List<(string Environment, string SessionId, string ReceiptText, LinklyBankReceiptKind Kind, string? CardType, string? MaskedCardNumber, string? ResponseCode, string? ResponseText)> Prints { get; } = [];

        public List<ReceiptPrintResult> Results { get; } = [];

        public Task<ReceiptPrintResult> PrintAsync(
            string environment,
            string sessionId,
            string receiptText,
            LinklyBankReceiptKind kind = LinklyBankReceiptKind.SignatureRequired,
            string? cardType = null,
            string? maskedCardNumber = null,
            string? responseCode = null,
            string? responseText = null,
            CancellationToken cancellationToken = default)
        {
            Prints.Add((environment, sessionId, receiptText, kind, cardType, maskedCardNumber, responseCode, responseText));
            if (Results.Count == 0)
            {
                return Task.FromResult(new ReceiptPrintResult(true, "printed"));
            }

            var result = Results[0];
            Results.RemoveAt(0);
            return Task.FromResult(result);
        }
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

    private static void AssertContainsOperationCancelledLog(
        IReadOnlyList<string> lines,
        bool transactionSubmitted,
        bool businessTimeoutCancelled)
    {
        var logLine = Assert.Single(lines, line => line.Contains("operation-cancelled", StringComparison.Ordinal));
        Assert.Contains("source=OperationCanceledException", logLine, StringComparison.Ordinal);
        Assert.Contains($"transactionSubmitted={transactionSubmitted}", logLine, StringComparison.Ordinal);
        Assert.Contains($"businessTimeoutCancelled={businessTimeoutCancelled}", logLine, StringComparison.Ordinal);
        Assert.Contains("localCancelRequested=False", logLine, StringComparison.Ordinal);
        Assert.Contains("callerCancelled=False", logLine, StringComparison.Ordinal);
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly object syncRoot = new();
        private readonly List<string> lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (syncRoot)
                {
                    return lines.ToArray();
                }
            }
        }

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (syncRoot)
            {
                lines.Add(line);
            }
        }
    }
}
