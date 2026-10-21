using System.Net;
using System.Text;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Square;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

// 「收银机直连 Square」（默认关闭）的纯逻辑测试：本机状态存储、Hbpos API 客户端、直连网关、设置页服务。
// 这些类都不依赖 WPF，macOS 上也能直接跑；与 ConfiguredCardTerminalClient 的接线测试在 ConfiguredCardTerminalClientTests.SquareDirect.cs。
public sealed class SquareDirectTests
{
    private const string SecretToken = "EAAA-secret-direct-token-0001";

    // ===== 本机状态存储 =====

    [Fact]
    public async Task Store_is_disabled_by_default_and_ignores_garbage_values()
    {
        var harness = new DirectHarness();

        Assert.False(await harness.Store.IsLocalEnabledAsync());

        await harness.Repository.SetValueAsync("CardTerminal:SquareDirect:Enabled", "yes-please");
        Assert.False(await harness.Store.IsLocalEnabledAsync());

        await harness.Store.SetLocalEnabledAsync(true);
        Assert.True(await harness.Store.IsLocalEnabledAsync());
    }

    [Fact]
    public async Task Store_saves_credential_only_as_protected_ciphertext_and_round_trips_it()
    {
        var harness = new DirectHarness();
        var validUntil = harness.Time.GetUtcNow().AddHours(12);

        await harness.Store.SaveCredentialAsync(new SquareDirectCredential(
            CardTerminalEnvironment.Production, SecretToken, 42, validUntil));

        // 落库的只能是 DPAPI 密文：任何存储项里都不能出现令牌明文。
        Assert.DoesNotContain(harness.Repository.AllValues, value => value.Contains(SecretToken, StringComparison.Ordinal));
        var loaded = await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production);
        Assert.NotNull(loaded);
        Assert.Equal(SecretToken, loaded.AccessToken);
        Assert.Equal(42, loaded.CredentialVersion);
        Assert.Equal(validUntil, loaded.ValidUntil);
        // 另一个环境互不影响。
        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Sandbox));
    }

    [Fact]
    public async Task Store_refuses_to_save_when_protection_fails_instead_of_storing_plaintext()
    {
        var harness = new DirectHarness();
        harness.Protector.FailProtect = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Store.SaveCredentialAsync(
            new SquareDirectCredential(CardTerminalEnvironment.Production, SecretToken, 1, harness.Time.GetUtcNow().AddHours(1))));

        Assert.Empty(harness.Repository.AllValues);
    }

    [Fact]
    public async Task Store_deletes_expired_credential_on_load()
    {
        var harness = new DirectHarness();
        await harness.Store.SaveCredentialAsync(new SquareDirectCredential(
            CardTerminalEnvironment.Production, SecretToken, 1, harness.Time.GetUtcNow().AddHours(12)));

        harness.Time.Advance(TimeSpan.FromHours(12));

        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
        Assert.Empty(harness.Repository.AllValues);
    }

    [Fact]
    public async Task Store_treats_undecryptable_credential_as_missing_and_removes_it()
    {
        var harness = new DirectHarness();
        await harness.Repository.SetValueAsync("CardTerminal:SquareDirect:CredentialProtected:Production", "not-our-ciphertext");

        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
        Assert.Empty(harness.Repository.AllValues);
    }

    [Fact]
    public async Task Store_turning_local_switch_off_deletes_credentials_of_all_environments()
    {
        var harness = new DirectHarness();
        await harness.Store.SetLocalEnabledAsync(true);
        foreach (var environment in new[] { CardTerminalEnvironment.Production, CardTerminalEnvironment.Sandbox })
        {
            await harness.Store.SaveCredentialAsync(new SquareDirectCredential(
                environment, SecretToken, 1, harness.Time.GetUtcNow().AddHours(1)));
        }

        await harness.Store.SetLocalEnabledAsync(false);

        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Sandbox));
        Assert.False(await harness.Store.IsLocalEnabledAsync());
    }

    [Fact]
    public async Task Store_server_state_defaults_to_unknown_and_unknown_clears_it()
    {
        var harness = new DirectHarness();

        Assert.Equal(SquareDirectServerState.Unknown, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Production));
        await harness.Store.SetServerStateAsync(CardTerminalEnvironment.Production, SquareDirectServerState.Disabled);
        Assert.Equal(SquareDirectServerState.Disabled, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Production));
        Assert.Equal(SquareDirectServerState.Unknown, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Sandbox));

        await harness.Store.SetServerStateAsync(CardTerminalEnvironment.Production, SquareDirectServerState.Unknown);
        Assert.Equal(SquareDirectServerState.Unknown, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Production));
    }

    [Fact]
    public async Task Store_pending_origins_deduplicate_expire_and_are_capped()
    {
        var harness = new DirectHarness();
        var now = harness.Time.GetUtcNow();

        await harness.Store.AddPendingOriginAsync(new SquareDirectPendingOrigin(CardTerminalEnvironment.Production, "c-1", now));
        await harness.Store.AddPendingOriginAsync(new SquareDirectPendingOrigin(CardTerminalEnvironment.Production, "c-1", now));
        Assert.Single(await harness.Store.ListPendingOriginsAsync());

        await harness.Store.RemovePendingOriginAsync(CardTerminalEnvironment.Production, "c-1");
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
        Assert.Empty(harness.Repository.AllValues);

        for (var i = 0; i < 25; i++)
        {
            await harness.Store.AddPendingOriginAsync(new SquareDirectPendingOrigin(
                CardTerminalEnvironment.Production, $"c-{i}", now.AddSeconds(i)));
        }

        var capped = await harness.Store.ListPendingOriginsAsync();
        Assert.Equal(20, capped.Count);
        Assert.DoesNotContain(capped, item => item.CheckoutId == "c-0");

        harness.Time.Advance(TimeSpan.FromHours(25));
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    [Fact]
    public void Credential_to_string_never_contains_the_token()
    {
        var credential = new SquareDirectCredential(CardTerminalEnvironment.Production, SecretToken, 9, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain(SecretToken, credential.ToString(), StringComparison.Ordinal);
    }

    // ===== Hbpos API 客户端 =====

    [Fact]
    public async Task Api_client_reads_credential_and_posts_environment()
    {
        string? body = null;
        string? path = null;
        var client = new SquareDirectApiClient(new HttpClient(new DirectStubHandler((request, _) =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{ "success": true, "data": { "environment": "Production", "accessToken": "tok-1", "credentialVersion": 5, "validUntil": "2026-10-22T00:00:00+00:00" } }""");
        })) { BaseAddress = new Uri("http://localhost/") });

        var credential = await client.GetCredentialAsync(CardTerminalEnvironment.Production);

        Assert.Equal("/api/v1/square/direct-credential", path);
        Assert.Contains("\"environment\":\"Production\"", body, StringComparison.Ordinal);
        Assert.Equal("tok-1", credential.AccessToken);
        Assert.Equal(5, credential.CredentialVersion);
    }

    [Fact]
    public async Task Api_client_maps_disabled_error_code_and_does_not_leak_body()
    {
        var client = new SquareDirectApiClient(new HttpClient(new DirectStubHandler((_, _) =>
            Json(HttpStatusCode.Forbidden, """{ "success": false, "errorCode": "SQUARE_DIRECT_DISABLED", "message": "off" }""")))
        { BaseAddress = new Uri("http://localhost/") });

        var ex = await Assert.ThrowsAsync<SquareDirectApiException>(() => client.GetCredentialAsync(CardTerminalEnvironment.Production));

        Assert.True(ex.IsDisabled);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task Api_client_turns_gateway_error_pages_into_non_disabled_exception()
    {
        var client = new SquareDirectApiClient(new HttpClient(new DirectStubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") }))
        { BaseAddress = new Uri("http://localhost/") });

        var ex = await Assert.ThrowsAsync<SquareDirectApiException>(() => client.RegisterOriginAsync(CardTerminalEnvironment.Production, "c/1"));

        Assert.False(ex.IsDisabled);
        Assert.DoesNotContain("bad gateway", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_client_escapes_checkout_id_in_origin_path()
    {
        string? rawPath = null;
        var client = new SquareDirectApiClient(new HttpClient(new DirectStubHandler((request, _) =>
        {
            rawPath = request.RequestUri!.AbsolutePath;
            return Json("""{ "success": true, "data": { "checkoutId": "c/1", "environment": "Production", "storeCode": "S01", "deviceCode": "POS1" } }""");
        })) { BaseAddress = new Uri("http://localhost/") });

        await client.RegisterOriginAsync(CardTerminalEnvironment.Sandbox, "c/1");

        Assert.Equal("/api/v1/square/checkouts/c%2F1/origin", rawPath);
    }

    // ===== 直连网关：路由、请求形状与回落 =====

    [Fact]
    public async Task Gateway_does_nothing_when_local_switch_is_off()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false);

        var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.Null(response);
        Assert.Empty(harness.SquareRequests);
    }

    [Fact]
    public async Task Gateway_falls_back_when_there_is_no_credential()
    {
        var harness = await DirectHarness.WithCredentialAsync(saveCredential: false);

        var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.Null(response);
        Assert.Empty(harness.SquareRequests);
    }

    [Theory]
    [InlineData("api/v1/square/refunds")]
    [InlineData("api/v1/square/refunds/r-1?environment=Production")]
    [InlineData("api/v1/health")]
    [InlineData("api/v1/square/checkouts/c-1/unknown")]
    public async Task Gateway_ignores_routes_that_must_stay_on_the_server(string relativeUrl)
    {
        var harness = await DirectHarness.WithCredentialAsync();

        var method = relativeUrl.EndsWith("refunds", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get;
        var response = await harness.Gateway.TrySendAsync(method, relativeUrl, new SquareRefundRequest("Production", "k", "p", new SquareMoneyDto(1, "AUD")), default);

        Assert.Null(response);
        Assert.Empty(harness.SquareRequests);
    }

    [Fact]
    public async Task Gateway_create_sends_the_exact_server_payload_with_bearer_and_version_then_registers_origin()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "PENDING" } }""");

        using var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"co-1\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var sent = Assert.Single(harness.SquareRequests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://connect.squareup.com/v2/terminals/checkouts", sent.Uri);
        Assert.Equal($"Bearer {SecretToken}", sent.Authorization);
        Assert.Equal(CardTerminalSettings.SquareVersion, sent.SquareVersion);
        // 与 POS API SquareTerminalRestClient.CreateCheckoutAsync 的请求体逐字段一致（含 null 字段）：
        // 这是回落中转后同一幂等键仍命中同一笔结账的前提。
        Assert.Equal(
            """{"idempotency_key":"idem-1","checkout":{"amount_money":{"amount":500,"currency":"AUD"},"device_options":{"device_id":"dev-1"},"location_id":"loc-1","reference_id":"POS1-20261021","note":"HBPOS S01 POS1","order_id":null}}""",
            sent.Body);

        await harness.WaitForOriginAsync();
        Assert.Equal(["co-1"], harness.Api.OriginCalls);
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    [Fact]
    public async Task Gateway_create_uses_sandbox_base_url_and_rewrites_unknown_sandbox_device_like_the_server()
    {
        var harness = await DirectHarness.WithCredentialAsync(CardTerminalEnvironment.Sandbox);
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-sb", "status": "PENDING" } }""");

        using var response = await harness.Gateway.TrySendAsync(
            HttpMethod.Post,
            "api/v1/square/checkouts",
            CreateRequest(environment: "Sandbox", deviceId: "some-paired-device"),
            default);

        Assert.NotNull(response);
        var sent = Assert.Single(harness.SquareRequests);
        Assert.Equal("https://connect.squareupsandbox.com/v2/terminals/checkouts", sent.Uri);
        Assert.Contains($"\"device_id\":\"{SquareSandboxTerminalDeviceIds.CreditCardSuccess}\"", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_get_cancel_and_dismiss_map_to_square_terminal_endpoints()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "IN_PROGRESS" } }""");

        using var get = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/checkouts/co-1?environment=Production", null, default);
        using var cancel = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts/co-1/cancel", new SquareCheckoutActionRequest("Production"), default);
        using var dismiss = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts/co-1/dismiss", new SquareCheckoutActionRequest("Production"), default);

        Assert.NotNull(get);
        Assert.NotNull(cancel);
        Assert.NotNull(dismiss);
        Assert.Equal(
            [
                "GET https://connect.squareup.com/v2/terminals/checkouts/co-1",
                "POST https://connect.squareup.com/v2/terminals/checkouts/co-1/cancel",
                "POST https://connect.squareup.com/v2/terminals/checkouts/co-1/dismiss"
            ],
            harness.SquareRequests.Select(r => $"{r.Method} {r.Uri}").ToArray());
        Assert.Equal("{}", harness.SquareRequests[1].Body);
        // 只读 / 取消 / 关闭不是「创建」，不触发归属登记。
        Assert.Empty(harness.Api.OriginCalls);
    }

    [Fact]
    public async Task Gateway_payment_lookup_goes_directly_to_square_payments_endpoint()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "payment": { "id": "p-1", "status": "COMPLETED" } }""");

        using var response = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/payments/p-1?environment=Production", null, default);

        Assert.NotNull(response);
        Assert.Contains("\"p-1\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var sent = Assert.Single(harness.SquareRequests);
        Assert.Equal("GET https://connect.squareup.com/v2/payments/p-1", $"{sent.Method} {sent.Uri}");
        Assert.Empty(harness.Api.OriginCalls);
    }

    [Fact]
    public async Task Gateway_payment_lookup_with_unreadable_body_falls_back_to_relay()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1" } }""");

        var response = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/payments/p-1?environment=Production", null, default);

        Assert.Null(response);
    }

    [Fact]
    public async Task Gateway_payment_lookup_forbidden_falls_back_but_keeps_credential_and_direct_mode()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json(HttpStatusCode.Forbidden, """{ "errors": [ { "code": "INSUFFICIENT_SCOPES" } ] }""");

        var first = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/payments/p-1?environment=Production", null, default);

        Assert.Null(first);
        // 权限不足只影响付款详情，结账接口用同一令牌仍然通，凭据与冷却状态都不能被动。
        Assert.NotNull(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "IN_PROGRESS" } }""");
        using var checkout = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/checkouts/co-1?environment=Production", null, default);
        Assert.NotNull(checkout);
    }

    [Fact]
    public async Task Gateway_network_failure_falls_back_then_cools_down_without_touching_square_again()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => throw new HttpRequestException("down");

        var first = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        var second = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/checkouts/co-1?environment=Production", null, default);

        Assert.Null(first);
        Assert.Null(second);
        // 冷却期内第二个请求根本不会再发给 Square，避免每次轮询都先白等一次直连超时。
        Assert.Single(harness.SquareRequests);

        harness.Time.Advance(SquareDirectGateway.NetworkFailureCooldown + TimeSpan.FromSeconds(1));
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "IN_PROGRESS" } }""");
        using var third = await harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/checkouts/co-1?environment=Production", null, default);

        Assert.NotNull(third);
        Assert.Equal(2, harness.SquareRequests.Count);
    }

    [Fact]
    public async Task Gateway_direct_timeout_falls_back_to_relay()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.HangSquare = true;

        var task = harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        await harness.AdvanceUntilCompletedAsync(task, SquareDirectGateway.CreateTimeout + TimeSpan.FromSeconds(1));

        Assert.Null(await task);
        // 超时后既不留下待补登的归属（没有拿到 checkoutId），也进入冷却。
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    [Fact]
    public async Task Gateway_caller_cancellation_is_not_swallowed()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.HangSquare = true;
        using var cts = new CancellationTokenSource();

        var task = harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Gateway_square_auth_rejection_deletes_credential_and_falls_back(HttpStatusCode status)
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json(status, """{ "errors": [ { "code": "UNAUTHORIZED" } ] }""");

        var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.Null(response);
        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
    }

    [Fact]
    public async Task Gateway_explicit_client_error_falls_back_without_cooldown()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json(HttpStatusCode.BadRequest, """{ "errors": [ { "code": "BAD_REQUEST" } ] }""");

        var first = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        var second = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, harness.SquareRequests.Count);
        Assert.NotNull(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Gateway_transient_http_status_falls_back_and_starts_cooldown(HttpStatusCode status)
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json(status, "{}");

        var first = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        var second = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Single(harness.SquareRequests);
    }

    [Fact]
    public async Task Gateway_unreadable_success_body_falls_back_and_does_not_register_origin()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "unexpected": true }""");

        var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.Null(response);
        Assert.Empty(harness.Api.OriginCalls);
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    // ===== 直连网关：归属登记 =====

    [Fact]
    public async Task Gateway_persists_pending_origin_before_returning_so_a_crash_cannot_lose_it()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "PENDING" } }""");
        harness.Api.OriginGate = new TaskCompletionSource();

        using var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);

        Assert.NotNull(response);
        var pending = Assert.Single(await harness.Store.ListPendingOriginsAsync());
        Assert.Equal("co-1", pending.CheckoutId);

        harness.Api.OriginGate.SetResult();
        await harness.WaitForOriginAsync();
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    [Fact]
    public async Task Gateway_relay_fallback_for_a_checkout_waits_until_origin_registration_finishes()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "PENDING" } }""");
        harness.Api.OriginGate = new TaskCompletionSource();
        using var created = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        Assert.NotNull(created);

        // 之后直连失败，要回落到中转轮询：中转按「门店 + 设备」校验归属，必须先等登记完成。
        harness.RespondSquare = _ => throw new HttpRequestException("down");
        var fallback = harness.Gateway.TrySendAsync(HttpMethod.Get, "api/v1/square/checkouts/co-1?environment=Production", null, default);
        await Task.Delay(100);
        Assert.False(fallback.IsCompleted);

        harness.Api.OriginGate.SetResult();

        Assert.Null(await fallback.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["co-1"], harness.Api.OriginCalls);
    }

    [Fact]
    public async Task Gateway_origin_disabled_removes_credential_and_marks_server_state_disabled()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "PENDING" } }""");
        harness.Api.OriginFailure = () => new SquareDirectApiException(HttpStatusCode.Forbidden, SquareDirectApiClient.DisabledErrorCode, "off");

        using var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        await harness.WaitForOriginAsync();

        Assert.NotNull(response);
        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
        Assert.Equal(SquareDirectServerState.Disabled, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Production));
    }

    [Fact]
    public async Task Gateway_origin_rejection_drops_pending_record_without_retrying()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "PENDING" } }""");
        harness.Api.OriginFailure = () => new SquareDirectApiException(HttpStatusCode.Forbidden, "SQUARE_DIRECT_ORIGIN_MISMATCH", "mismatch");

        using var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        await harness.WaitForOriginAsync();

        Assert.NotNull(response);
        Assert.Single(harness.Api.OriginCalls);
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    [Fact]
    public async Task Gateway_origin_network_failure_retries_three_times_then_keeps_the_pending_record()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.RespondSquare = _ => Json("""{ "checkout": { "id": "co-1", "status": "PENDING" } }""");
        harness.Api.OriginFailure = () => new HttpRequestException("down");

        using var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        await harness.WaitForOriginAsync(advanceTime: true);

        Assert.NotNull(response);
        Assert.Equal(3, harness.Api.OriginCalls.Count);
        Assert.Single(await harness.Store.ListPendingOriginsAsync());
    }

    [Fact]
    public async Task Gateway_warm_up_flushes_pending_origins_left_by_an_earlier_run()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        await harness.Store.AddPendingOriginAsync(new SquareDirectPendingOrigin(
            CardTerminalEnvironment.Production, "old-co", harness.Time.GetUtcNow()));

        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);

        Assert.Equal(["old-co"], harness.Api.OriginCalls);
        Assert.Empty(await harness.Store.ListPendingOriginsAsync());
    }

    // ===== 直连网关：凭据领取 =====

    [Fact]
    public async Task Gateway_warm_up_does_nothing_when_local_switch_is_off()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false, saveCredential: false);

        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);

        Assert.Equal(0, harness.Api.CredentialCalls);
    }

    [Fact]
    public async Task Gateway_warm_up_fetches_credential_when_missing_and_saves_it_encrypted()
    {
        var harness = await DirectHarness.WithCredentialAsync(saveCredential: false);

        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);

        Assert.Equal(1, harness.Api.CredentialCalls);
        var credential = await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production);
        Assert.NotNull(credential);
        Assert.Equal(SecretToken, credential.AccessToken);
        Assert.DoesNotContain(harness.Repository.AllValues, value => value.Contains(SecretToken, StringComparison.Ordinal));
        Assert.Equal(SquareDirectServerState.Enabled, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Production));
    }

    [Fact]
    public async Task Gateway_warm_up_keeps_a_fresh_credential_and_refreshes_an_aging_one_with_spacing()
    {
        var harness = await DirectHarness.WithCredentialAsync();

        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);
        Assert.Equal(0, harness.Api.CredentialCalls);

        // 凭据「年龄」超过 30 分钟（剩余不足 11.5 小时）就重新领取，让总部关闭开关尽快生效。
        harness.Time.Advance(TimeSpan.FromMinutes(31));
        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);
        Assert.Equal(1, harness.Api.CredentialCalls);

        // 领取失败也要遵守最短间隔，避免每次进付款页都去撞服务端的每小时 10 次限流。
        harness.Api.CredentialFailure = () => new HttpRequestException("down");
        harness.Time.Advance(TimeSpan.FromMinutes(31));
        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);
        Assert.Equal(2, harness.Api.CredentialCalls);
        await harness.Gateway.WarmUpAsync(CardTerminalEnvironment.Production, default);
        Assert.Equal(2, harness.Api.CredentialCalls);
    }

    [Fact]
    public async Task Gateway_refresh_when_store_switch_is_off_deletes_credential_and_reports_disabled()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.Api.CredentialFailure = () => new SquareDirectApiException(HttpStatusCode.Forbidden, SquareDirectApiClient.DisabledErrorCode, "off");

        var outcome = await harness.Gateway.RefreshCredentialAsync(CardTerminalEnvironment.Production, default);

        Assert.Equal(SquareDirectRefreshOutcome.ServerDisabled, outcome);
        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
        Assert.Equal(SquareDirectServerState.Disabled, await harness.Store.GetServerStateAsync(CardTerminalEnvironment.Production));
        // 之后的付款不再尝试直连。
        var response = await harness.Gateway.TrySendAsync(HttpMethod.Post, "api/v1/square/checkouts", CreateRequest(), default);
        Assert.Null(response);
        Assert.Empty(harness.SquareRequests);
    }

    [Fact]
    public async Task Gateway_refresh_failure_keeps_the_existing_credential()
    {
        var harness = await DirectHarness.WithCredentialAsync();
        harness.Api.CredentialFailure = () => new SquareDirectApiException(HttpStatusCode.TooManyRequests, "SQUARE_DIRECT_RATE_LIMITED", "slow down");

        var outcome = await harness.Gateway.RefreshCredentialAsync(CardTerminalEnvironment.Production, default);

        Assert.Equal(SquareDirectRefreshOutcome.Failed, outcome);
        Assert.NotNull(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
    }

    [Theory]
    [InlineData("", "Production")]
    [InlineData(SecretToken, "Sandbox")]
    public async Task Gateway_refresh_rejects_malformed_responses(string token, string environment)
    {
        var harness = await DirectHarness.WithCredentialAsync(saveCredential: false);
        harness.Api.CredentialResponse = new SquareDirectCredentialResponse(
            environment, token, 1, harness.Time.GetUtcNow().AddHours(12));

        var outcome = await harness.Gateway.RefreshCredentialAsync(CardTerminalEnvironment.Production, default);

        Assert.Equal(SquareDirectRefreshOutcome.Failed, outcome);
        Assert.Null(await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production));
    }

    [Fact]
    public async Task Gateway_refresh_clamps_an_excessive_valid_until_to_twelve_hours()
    {
        var harness = await DirectHarness.WithCredentialAsync(saveCredential: false);
        harness.Api.CredentialResponse = new SquareDirectCredentialResponse(
            "Production", SecretToken, 1, harness.Time.GetUtcNow().AddDays(30));

        await harness.Gateway.RefreshCredentialAsync(CardTerminalEnvironment.Production, default);

        var credential = await harness.Store.LoadCredentialAsync(CardTerminalEnvironment.Production);
        Assert.NotNull(credential);
        Assert.Equal(harness.Time.GetUtcNow().AddHours(12), credential.ValidUntil);
    }

    // ===== 设置页服务 =====

    [Fact]
    public async Task Settings_service_reports_default_off_status()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false, saveCredential: false);

        var status = await harness.Settings.GetStatusAsync(CardTerminalEnvironment.Production);

        Assert.False(status.LocalEnabled);
        Assert.Equal(SquareDirectServerState.Unknown, status.ServerState);
        Assert.False(status.HasCredential);
        Assert.Null(status.Remaining);
        Assert.False(status.IsActive);
    }

    [Fact]
    public async Task Settings_service_turning_on_fetches_a_credential_and_reports_remaining_validity()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false, saveCredential: false);

        var status = await harness.Settings.SetEnabledAsync(true, CardTerminalEnvironment.Production);

        Assert.True(status.IsActive);
        Assert.Equal(SquareDirectServerState.Enabled, status.ServerState);
        Assert.Equal(TimeSpan.FromHours(12), status.Remaining);
    }

    [Fact]
    public async Task Settings_service_turning_on_when_store_switch_is_off_shows_server_disabled()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false, saveCredential: false);
        harness.Api.CredentialFailure = () => new SquareDirectApiException(HttpStatusCode.Forbidden, SquareDirectApiClient.DisabledErrorCode, "off");

        var status = await harness.Settings.SetEnabledAsync(true, CardTerminalEnvironment.Production);

        Assert.True(status.LocalEnabled);
        Assert.Equal(SquareDirectServerState.Disabled, status.ServerState);
        Assert.False(status.HasCredential);
        Assert.False(status.IsActive);
    }

    [Fact]
    public async Task Settings_service_turning_on_while_offline_keeps_server_state_unknown()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false, saveCredential: false);
        harness.Api.CredentialFailure = () => new HttpRequestException("down");

        var status = await harness.Settings.SetEnabledAsync(true, CardTerminalEnvironment.Production);

        Assert.True(status.LocalEnabled);
        Assert.Equal(SquareDirectServerState.Unknown, status.ServerState);
        Assert.False(status.IsActive);
    }

    [Fact]
    public async Task Settings_service_turning_off_removes_the_credential_without_calling_the_server()
    {
        var harness = await DirectHarness.WithCredentialAsync();

        var status = await harness.Settings.SetEnabledAsync(false, CardTerminalEnvironment.Production);

        Assert.False(status.LocalEnabled);
        Assert.False(status.HasCredential);
        Assert.Equal(0, harness.Api.CredentialCalls);
    }

    [Fact]
    public async Task Settings_service_refresh_is_a_noop_while_local_switch_is_off()
    {
        var harness = await DirectHarness.WithCredentialAsync(enabled: false, saveCredential: false);

        var status = await harness.Settings.RefreshAsync(CardTerminalEnvironment.Production);

        Assert.False(status.LocalEnabled);
        Assert.Equal(0, harness.Api.CredentialCalls);
    }

    // ===== 辅助 =====

    internal static SquareCreateCheckoutRequest CreateRequest(
        string environment = "Production",
        string deviceId = "dev-1") =>
        new(
            environment,
            "idem-1",
            deviceId,
            "loc-1",
            new SquareMoneyDto(500, "AUD"),
            ReferenceId: "POS1-20261021",
            Note: "HBPOS S01 POS1");

    internal static HttpResponseMessage Json(string body) => Json(HttpStatusCode.OK, body);

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? SquareVersion,
        string Body);

    internal sealed class DirectStubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request, cancellationToken));
    }

    internal sealed class DirectHarness
    {
        private readonly List<CapturedRequest> _squareRequests = [];

        public DirectHarness()
        {
            Time = new FakeTimeProvider();
            Repository = new InMemoryDirectSettingsRepository();
            Protector = new ReversibleProtector();
            Store = new SquareDirectCredentialStore(Repository, Protector, Time);
            Api = new FakeDirectApi(Time, SecretToken);
            var handler = new SquareHandler(this);
            Gateway = new SquareDirectGateway(Store, Api, new HttpClient(handler), Time);
            Settings = new SquareDirectSettingsService(Store, Gateway, Time);
        }

        public FakeTimeProvider Time { get; }

        public InMemoryDirectSettingsRepository Repository { get; }

        public ReversibleProtector Protector { get; }

        public SquareDirectCredentialStore Store { get; }

        public FakeDirectApi Api { get; }

        public SquareDirectGateway Gateway { get; }

        public SquareDirectSettingsService Settings { get; }

        public IReadOnlyList<CapturedRequest> SquareRequests => _squareRequests;

        public Func<HttpRequestMessage, HttpResponseMessage> RespondSquare { get; set; } =
            _ => Json(HttpStatusCode.InternalServerError, "{}");

        /// <summary>为 true 时直连请求一直挂起，直到被超时或取消。</summary>
        public bool HangSquare { get; set; }

        public static async Task<DirectHarness> WithCredentialAsync(
            CardTerminalEnvironment environment = CardTerminalEnvironment.Production,
            bool enabled = true,
            bool saveCredential = true)
        {
            var harness = new DirectHarness();
            if (enabled)
            {
                await harness.Store.SetLocalEnabledAsync(true);
            }

            if (saveCredential)
            {
                await harness.Store.SaveCredentialAsync(new SquareDirectCredential(
                    environment, SecretToken, 7, harness.Time.GetUtcNow().AddHours(12)));
            }

            return harness;
        }

        /// <summary>等后台的归属登记结束。advanceTime 为 true 时逐秒推进虚拟时间，让登记重试的退避等待结束。</summary>
        public async Task WaitForOriginAsync(bool advanceTime = false)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                if (Gateway.InFlightOriginCount == 0)
                {
                    return;
                }

                if (advanceTime)
                {
                    Time.Advance(TimeSpan.FromSeconds(1));
                }
            }

            throw new TimeoutException("Origin registration did not finish.");
        }

        public async Task AdvanceUntilCompletedAsync<T>(Task<T> task, TimeSpan virtualDuration)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                if (task.IsCompleted)
                {
                    break;
                }

                Time.Advance(virtualDuration);
            }

            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private sealed class SquareHandler(DirectHarness owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                owner._squareRequests.Add(new CapturedRequest(
                    request.Method,
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("Square-Version", out var versions) ? versions.Single() : null,
                    body));
                if (owner.HangSquare)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return owner.RespondSquare(request);
            }
        }
    }

    internal sealed class FakeDirectApi(TimeProvider time, string token) : ISquareDirectApiClient
    {
        private readonly List<string> _originCalls = [];
        private int _credentialCalls;

        public int CredentialCalls => Volatile.Read(ref _credentialCalls);

        public IReadOnlyList<string> OriginCalls
        {
            get
            {
                lock (_originCalls)
                {
                    return _originCalls.ToArray();
                }
            }
        }

        public SquareDirectCredentialResponse? CredentialResponse { get; set; }

        public Func<Exception>? CredentialFailure { get; set; }

        public Func<Exception>? OriginFailure { get; set; }

        /// <summary>设置后，归属登记会一直等到它完成，用来验证「回落前要等登记」。</summary>
        public TaskCompletionSource? OriginGate { get; set; }

        public Task<SquareDirectCredentialResponse> GetCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _credentialCalls);
            if (CredentialFailure is { } failure)
            {
                return Task.FromException<SquareDirectCredentialResponse>(failure());
            }

            return Task.FromResult(CredentialResponse ?? new SquareDirectCredentialResponse(
                environment.ToString(), token, 7, time.GetUtcNow().AddHours(12)));
        }

        public async Task RegisterOriginAsync(
            CardTerminalEnvironment environment,
            string checkoutId,
            CancellationToken cancellationToken = default)
        {
            lock (_originCalls)
            {
                _originCalls.Add(checkoutId);
            }

            if (OriginGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (OriginFailure is { } failure)
            {
                throw failure();
            }
        }
    }

    internal sealed class ReversibleProtector : IDeviceAuthorizationProtector
    {
        public bool FailProtect { get; set; }

        public string? Protect(string? value) =>
            FailProtect || string.IsNullOrWhiteSpace(value)
                ? null
                : "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        public string? Unprotect(string? protectedValue) =>
            protectedValue?.StartsWith("protected:", StringComparison.Ordinal) == true
                ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["protected:".Length..]))
                : null;
    }

    internal sealed class InMemoryDirectSettingsRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> AllValues
        {
            get
            {
                lock (_values)
                {
                    return _values.Values.ToArray();
                }
            }
        }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_values)
            {
                return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
            }
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            lock (_values)
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            lock (_values)
            {
                foreach (var (key, value) in values)
                {
                    _values[key] = value;
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_values)
            {
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }
    }
}
