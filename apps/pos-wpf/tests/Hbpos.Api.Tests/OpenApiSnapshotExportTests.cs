using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.DailyClose;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hbpos.Api.Tests;

public sealed class OpenApiSnapshotExportTests
{
    [Fact]
    public async Task Swagger_document_can_be_exported_from_the_database_free_development_test_host()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/devices/register", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/app-updates/pos-ipad", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/app-updates/pos-handheld", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/app-updates/pos-handheld/ota", document, StringComparison.Ordinal);
        Assert.Contains("deviceSystem", document, StringComparison.Ordinal);
        Assert.Contains("catalogVersion", document, StringComparison.Ordinal);
        Assert.Contains("pageChecksum", document, StringComparison.Ordinal);
        Assert.Contains("LinklyCloudBackendCardTransactionDto", document, StringComparison.Ordinal);
        Assert.Contains("cardTransaction", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/held-orders/capabilities", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/held-orders/{holdGuid}/claims/prepare", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/held-orders/claims/mine", document, StringComparison.Ordinal);
        Assert.Contains("HeldOrderSourceDto", document, StringComparison.Ordinal);
        Assert.Contains("heldOrderDisposition", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/daily-closes/sync", document, StringComparison.Ordinal);
        Assert.Contains("DailyCloseSyncRequest", document, StringComparison.Ordinal);
        Assert.Contains("DailyCloseSyncResponse", document, StringComparison.Ordinal);

        AssertSharedSaleCartOpenApiContract(document);
        AssertDeviceActivationRequestOpenApiContract(document);

        var outputPath = Environment.GetEnvironmentVariable("HBPOS_OPENAPI_SNAPSHOT_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            await File.WriteAllTextAsync(outputPath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDeviceActivationPayloads))]
    public async Task Activation_request_structure_errors_return_400_before_business_service(
        string path,
        string json,
        bool authenticated)
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "TEST-AUTH");
            request.Headers.Add(DeviceAuthConstants.DeviceCodeHeader, "POS-OLD");
            request.Headers.Add(DeviceAuthConstants.StoreCodeHeader, "S001");
            request.Headers.Add(DeviceAuthConstants.HardwareIdHeader, "HW-1");
        }

        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 but received {(int)response.StatusCode}: {responseBody}");
        Assert.Equal(0, factory.ActivationService.CallCount);
    }

    public static IEnumerable<object[]> InvalidDeviceActivationPayloads()
    {
        const string preview = "/api/v1/devices/activation-code/preview";
        const string redeem = "/api/v1/devices/activation-code/redeem";
        const string rebind = "/api/v1/devices/activation-code/rebind";
        var overlongCode = new string('A', 129);
        var overlongSystem = new string('W', 21);
        var overlongHardware = new string('H', 101);
        var overlongTerminal = new string('T', 201);

        yield return [preview, "{\"deviceSystem\":\"Windows\"}", false];
        yield return [preview, "{\"activationCode\":\"\",\"deviceSystem\":\"Windows\"}", false];
        yield return [preview, JsonSerializer.Serialize(new { activationCode = overlongCode, deviceSystem = "Windows" }), false];
        yield return [preview, "{\"activationCode\":\"HBDEV1-CODE\"}", false];
        yield return [preview, "{\"activationCode\":\"HBDEV1-CODE\",\"deviceSystem\":\"\"}", false];
        yield return [preview, JsonSerializer.Serialize(new { activationCode = "HBDEV1-CODE", deviceSystem = overlongSystem }), false];

        yield return [redeem, "{\"hardwareId\":\"HW-1\",\"deviceSystem\":\"Windows\"}", false];
        yield return [redeem, "{\"activationCode\":\"\",\"hardwareId\":\"HW-1\",\"deviceSystem\":\"Windows\"}", false];
        yield return [redeem, JsonSerializer.Serialize(new { activationCode = overlongCode, hardwareId = "HW-1", deviceSystem = "Windows" }), false];
        yield return [redeem, "{\"activationCode\":\"HBDEV1-CODE\",\"deviceSystem\":\"Windows\"}", false];
        yield return [redeem, "{\"activationCode\":\"HBDEV1-CODE\",\"hardwareId\":\"\",\"deviceSystem\":\"Windows\"}", false];
        yield return [redeem, JsonSerializer.Serialize(new { activationCode = "HBDEV1-CODE", hardwareId = overlongHardware, deviceSystem = "Windows" }), false];
        yield return [redeem, "{\"activationCode\":\"HBDEV1-CODE\",\"hardwareId\":\"HW-1\"}", false];
        yield return [redeem, "{\"activationCode\":\"HBDEV1-CODE\",\"hardwareId\":\"HW-1\",\"deviceSystem\":\"\"}", false];
        yield return [redeem, JsonSerializer.Serialize(new { activationCode = "HBDEV1-CODE", hardwareId = "HW-1", deviceSystem = overlongSystem }), false];
        yield return [redeem, JsonSerializer.Serialize(new { activationCode = "HBDEV1-CODE", hardwareId = "HW-1", terminalName = overlongTerminal, deviceSystem = "Windows" }), false];

        yield return [rebind, "{}", true];
        yield return [rebind, "{\"activationCode\":\"\"}", true];
        yield return [rebind, JsonSerializer.Serialize(new { activationCode = overlongCode }), true];
        yield return [rebind, JsonSerializer.Serialize(new { activationCode = "HBDEV1-CODE", terminalName = overlongTerminal }), true];
    }

    [Fact]
    public async Task Activation_business_denial_with_valid_request_shape_remains_HTTP_200()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/devices/activation-code/preview",
            new DeviceActivationCodePreviewRequest("HBDEV1-CODE", DeviceSystems.Windows));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.ActivationService.CallCount);
    }

    [Fact]
    public async Task Reserved_activation_code_in_metadata_returns_documented_400_problem_body()
    {
        const string activationCode =
            "HBDEV1-00000000000000000000000000-00000000000000000000000000";
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/devices/activation-code/redeem",
            new DeviceActivationCodeRedeemRequest(
                activationCode,
                "HW-1",
                $"Counter {activationCode}",
                DeviceSystems.Windows));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "Invalid device metadata.",
            body.RootElement.GetProperty("title").GetString());
        Assert.Equal(0, factory.ActivationService.CallCount);
    }

    [Fact]
    public async Task Redeem_recovery_only_header_is_forwarded_without_changing_body_contract()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/devices/activation-code/redeem")
        {
            Content = JsonContent.Create(new DeviceActivationCodeRedeemRequest(
                "HBDEV1-CODE",
                "HW-1",
                null,
                DeviceSystems.Windows)),
        };
        request.Headers.Add("X-HBPOS-Activation-Recovery-Only", "true");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(factory.ActivationService.LastRecoveryOnly);
        Assert.Equal(1, factory.ActivationService.CallCount);
    }

    [Fact]
    public async Task Anonymous_activation_rate_limit_returns_429_after_the_fixed_window_budget()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        var payload = new DeviceActivationCodePreviewRequest(
            "HBDEV1-CODE",
            DeviceSystems.Windows);

        for (var requestNumber = 1; requestNumber <= 10; requestNumber++)
        {
            using var allowed = await client.PostAsJsonAsync(
                "/api/v1/devices/activation-code/preview",
                payload);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await client.PostAsJsonAsync(
            "/api/v1/devices/activation-code/preview",
            payload);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/json", rejected.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "Too many device activation requests.",
            problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(10, factory.ActivationService.CallCount);
    }

    [Fact]
    public async Task Rebind_without_device_identity_returns_the_documented_401_json_body()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/devices/activation-code/rebind",
            new DeviceActivationCodeRebindRequest("HBDEV1-CODE", null));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "DEVICE_AUTH_REQUIRED",
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(0, factory.ActivationService.CallCount);
    }

    [Fact]
    public async Task Daily_close_sync_binds_camel_case_json_and_scopes_the_call_to_the_device_claims()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var request = CreateDailyCloseRequest(DailyCloseJson("S001", "POS-OLD"), authenticated: true);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 but received {(int)response.StatusCode}: {body}");
        Assert.Equal(
            "{\"accepted\":true,\"alreadySynced\":false,\"replacedPlaceholder\":false}",
            body);
        var received = Assert.Single(factory.DailyCloseService.Calls);
        Assert.Equal("S001", received.StoreCode);
        Assert.Equal("POS-OLD", received.DeviceCode);
        Assert.Equal(new DateOnly(2026, 10, 7), received.Request.BusinessDate);
        Assert.Equal(11, received.Request.CashCounts.Count);
        Assert.Equal(3, received.Request.Tenders.Count);
    }

    [Fact]
    public async Task Daily_close_sync_rejects_body_scope_that_differs_from_the_device_with_403()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var request = CreateDailyCloseRequest(DailyCloseJson("S001", "POS-OTHER"), authenticated: true);

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", body.RootElement.GetProperty("code").GetString());
        Assert.Empty(factory.DailyCloseService.Calls);
    }

    [Fact]
    public async Task Daily_close_sync_without_device_authorization_returns_401_before_the_service()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var request = CreateDailyCloseRequest(DailyCloseJson("S001", "POS-OLD"), authenticated: false);

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("DEVICE_AUTH_REQUIRED", body.RootElement.GetProperty("code").GetString());
        Assert.Empty(factory.DailyCloseService.Calls);
    }

    [Theory]
    [InlineData("validation", HttpStatusCode.BadRequest, "INVALID_CASH_COUNTS")]
    [InlineData("conflict", HttpStatusCode.Conflict, "DAILY_CLOSE_CONTENT_CONFLICT")]
    public async Task Daily_close_sync_maps_service_failures_to_json_code_and_message(
        string kind,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var factory = new OpenApiExportFactory();
        factory.DailyCloseService.Failure = kind == "validation"
            ? new DailyCloseValidationException(expectedCode, "rejected")
            : new DailyCloseConflictException(expectedCode, "rejected");
        using var client = factory.CreateClient();
        using var request = CreateDailyCloseRequest(DailyCloseJson("S001", "POS-OLD"), authenticated: true);

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal("rejected", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Daily_close_sync_returns_400_for_a_structurally_incomplete_body_before_the_service()
    {
        using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();
        using var request = CreateDailyCloseRequest("{}", authenticated: true);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.DailyCloseService.Calls);
    }

    private static HttpRequestMessage CreateDailyCloseRequest(string json, bool authenticated)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/daily-closes/sync")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "TEST-AUTH");
            request.Headers.Add(DeviceAuthConstants.DeviceCodeHeader, "POS-OLD");
            request.Headers.Add(DeviceAuthConstants.StoreCodeHeader, "S001");
            request.Headers.Add(DeviceAuthConstants.HardwareIdHeader, "HW-1");
        }

        return request;
    }

    private static string DailyCloseJson(string storeCode, string deviceCode)
    {
        var counts = string.Join(
            ",",
            new[] { 10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10, 5 }
                .Select(cents => $"{{\"denominationCents\":{cents},\"quantity\":0}}"));
        return $$"""
            {
              "schemaVersion": 1,
              "dailyCloseGuid": "11111111-2222-3333-4444-555555555555",
              "storeCode": "{{storeCode}}",
              "deviceCode": "{{deviceCode}}",
              "clientKind": "Wpf",
              "businessDate": "2026-10-07",
              "periodFrom": "2026-10-07T00:00:00+11:00",
              "periodTo": "2026-10-08T00:00:00+11:00",
              "savedAt": "2026-10-07T20:00:00+11:00",
              "cashierId": "C001",
              "cashierName": "Alice",
              "appVersion": "1.0.47",
              "orderCount": 0,
              "returnQuantity": 0,
              "refundAmount": 0,
              "tenders": [
                { "method": "Cash", "salesAmount": 0, "refundAmount": 0, "netAmount": 0 },
                { "method": "Card", "salesAmount": 0, "refundAmount": 0, "netAmount": 0 },
                { "method": "Voucher", "salesAmount": 0, "refundAmount": 0, "netAmount": 0 }
              ],
              "cashCounts": [{{counts}}],
              "noteSubtotal": 0,
              "coinSubtotal": 0,
              "countedCashAmount": 0,
              "cashDifference": 0
            }
            """;
    }

    private static void AssertSharedSaleCartOpenApiContract(string document)
    {
        using var json = JsonDocument.Parse(document);
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("SharedSaleCartV1", out _), "OpenAPI 必须保留 SharedSaleCartV1 schema");
        Assert.True(schemas.TryGetProperty("SharedSaleCartV2", out _), "OpenAPI 必须生成 SharedSaleCartV2 schema");
        Assert.True(schemas.TryGetProperty("SharedPricingStateV1", out _), "OpenAPI 必须保留 SharedPricingStateV1 schema");
        Assert.True(schemas.TryGetProperty("SharedSaleLineV1", out _), "OpenAPI 必须保留 SharedSaleLineV1 schema");
        var v2Line = schemas.GetProperty("SharedSaleLineV2");
        Assert.Contains(
            "catalogDiscountBasisPoints",
            v2Line.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));

        AssertPayloadPropertyIsV1OrV2OneOf(json.RootElement, "SharedHeldOrderClaimPrepareResponse", "payload");
        AssertPayloadPropertyIsV1OrV2OneOf(json.RootElement, "SharedHeldOrderPublishRequest", "cart");
        AssertPayloadPropertyIsV1OrV2OneOf(json.RootElement, "SharedHeldOrderRecoveryClaimDto", "payload");
    }

    private static void AssertDeviceActivationRequestOpenApiContract(string document)
    {
        using var json = JsonDocument.Parse(document);
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        var preview = schemas.GetProperty(nameof(DeviceActivationCodePreviewRequest));
        AssertRequiredNonNullableString(preview, "activationCode", 128);
        AssertRequiredNonNullableString(preview, "deviceSystem", 20);

        var redeem = schemas.GetProperty(nameof(DeviceActivationCodeRedeemRequest));
        AssertRequiredNonNullableString(redeem, "activationCode", 128);
        AssertRequiredNonNullableString(redeem, "hardwareId", 100);
        AssertRequiredNonNullableString(redeem, "deviceSystem", 20);
        Assert.DoesNotContain(
            "terminalName",
            redeem.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            200,
            redeem.GetProperty("properties").GetProperty("terminalName")
                .GetProperty("maxLength").GetInt32());

        var rebind = schemas.GetProperty(nameof(DeviceActivationCodeRebindRequest));
        AssertRequiredNonNullableString(rebind, "activationCode", 128);
        Assert.DoesNotContain(
            "terminalName",
            rebind.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            200,
            rebind.GetProperty("properties").GetProperty("terminalName")
                .GetProperty("maxLength").GetInt32());

        foreach (var path in new[]
                 {
                     "/api/v1/devices/activation-code/preview",
                     "/api/v1/devices/activation-code/redeem",
                     "/api/v1/devices/activation-code/rebind",
                 })
        {
            var operation = json.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post");
            var requestBody = operation.GetProperty("requestBody");
            Assert.True(requestBody.GetProperty("required").GetBoolean(), $"{path} body 必须为 required");
            var responses = operation.GetProperty("responses");
            Assert.True(responses.TryGetProperty("200", out _), $"{path} 必须声明 200");
            Assert.True(responses.TryGetProperty("400", out _), $"{path} 必须声明 400");
            AssertJsonResponseSchema(responses, "400", "ProblemDetails");
        }

        foreach (var anonymousPath in new[]
                 {
                     "/api/v1/devices/activation-code/preview",
                     "/api/v1/devices/activation-code/redeem",
                 })
        {
            var responses = json.RootElement.GetProperty("paths")
                .GetProperty(anonymousPath)
                .GetProperty("post")
                .GetProperty("responses");
            Assert.True(responses.TryGetProperty("429", out _), $"{anonymousPath} 必须声明 429");
            AssertJsonResponseSchema(responses, "429", "ProblemDetails");
        }

        var rebindResponses = json.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/devices/activation-code/rebind")
            .GetProperty("post")
            .GetProperty("responses");
        Assert.True(rebindResponses.TryGetProperty("401", out _));
        AssertJsonResponseSchema(rebindResponses, "401", "ObjectApiResult");
        var forbidden = rebindResponses.GetProperty("403");
        Assert.False(forbidden.TryGetProperty("content", out _));

        var redeemOperation = json.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/devices/activation-code/redeem")
            .GetProperty("post");
        var recoveryHeader = Assert.Single(
            redeemOperation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("name").GetString()
                    == "X-HBPOS-Activation-Recovery-Only");
        Assert.False(
            recoveryHeader.TryGetProperty("required", out var headerRequired)
            && headerRequired.GetBoolean());
        Assert.Equal(
            "boolean",
            recoveryHeader.GetProperty("schema").GetProperty("type").GetString());
    }

    private static void AssertRequiredNonNullableString(
        JsonElement schema,
        string propertyName,
        int maximumLength)
    {
        Assert.Contains(
            propertyName,
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var property = schema.GetProperty("properties").GetProperty(propertyName);
        Assert.False(
            property.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean(),
            $"{propertyName} 不得声明 nullable");
        Assert.Equal(maximumLength, property.GetProperty("maxLength").GetInt32());
    }

    private static void AssertJsonResponseSchema(
        JsonElement responses,
        string statusCode,
        string schemaName)
    {
        var schemaReference = responses.GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        Assert.Equal($"#/components/schemas/{schemaName}", schemaReference);
    }

    private static void AssertPayloadPropertyIsV1OrV2OneOf(JsonElement documentRoot, string schemaName, string propertyName)
    {
        var schema = documentRoot.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        var property = schema.GetProperty("properties").GetProperty(propertyName);
        var oneOf = property.GetProperty("oneOf");
        Assert.False(
            property.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean(),
            $"{schemaName}.{propertyName} 不得声明 nullable");
        Assert.Equal(2, oneOf.GetArrayLength());
        var refs = oneOf.EnumerateArray().Select(e => e.GetProperty("$ref").GetString()).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        Assert.Equal("#/components/schemas/SharedSaleCartV1", refs[0]);
        Assert.Equal("#/components/schemas/SharedSaleCartV2", refs[1]);
    }

    private sealed class OpenApiExportFactory : WebApplicationFactory<Program>
    {
        public RecordingDeviceActivationCodeService ActivationService { get; } = new();

        public RecordingDailyCloseSyncService DailyCloseService { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                // 测试宿主必须覆盖 Development User Secrets，避免 OpenAPI 导出误连本机业务数据库。
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:MainConnection"] = string.Empty,
                    ["ConnectionStrings:DefaultConnection"] = string.Empty,
                    ["ConnectionStrings:PosmConnection"] = string.Empty,
                    ["ConnectionStrings:HBPOSMConnection"] = string.Empty
                });
            });
            builder.ConfigureServices(services =>
            {
                // 快照只能从测试宿主导出，所有启动期 schema 初始化均替换为无副作用实现。
                services.RemoveAll<IStoreSchemaInitializer>();
                services.AddSingleton<IStoreSchemaInitializer>(new NoOp());
                services.RemoveAll<IAttendanceQrKeySchemaInitializer>();
                services.AddSingleton<IAttendanceQrKeySchemaInitializer>(new NoOp());
                services.RemoveAll<IAdvertisementSchemaInitializer>();
                services.AddSingleton<IAdvertisementSchemaInitializer>(new NoOp());
                services.RemoveAll<ILinklyCloudCredentialSchemaInitializer>();
                services.AddSingleton<ILinklyCloudCredentialSchemaInitializer>(new NoOp());
                services.RemoveAll<IOperationAuditSchemaInitializer>();
                services.AddSingleton<IOperationAuditSchemaInitializer>(new NoOp());
                services.RemoveAll<IDeviceRuntimeStatusSchemaInitializer>();
                services.AddSingleton<IDeviceRuntimeStatusSchemaInitializer>(new NoOp());
                services.RemoveAll<ILinklyCloudBackendAsyncSchemaInitializer>();
                services.AddSingleton<ILinklyCloudBackendAsyncSchemaInitializer>(new NoOp());
                services.RemoveAll<ILinklySettlementSchemaInitializer>();
                services.AddSingleton<ILinklySettlementSchemaInitializer>(new NoOp());
                services.RemoveAll<IDailyCloseSchemaInitializer>();
                services.AddSingleton<IDailyCloseSchemaInitializer>(new NoOp());
                services.RemoveAll<IInstallmentRepaymentClaimSchemaInitializer>();
                services.AddSingleton<IInstallmentRepaymentClaimSchemaInitializer>(new NoOp());
                services.RemoveAll<IInstallmentCancelClaimSchemaInitializer>();
                services.AddSingleton<IInstallmentCancelClaimSchemaInitializer>(new NoOp());
                services.RemoveAll<ISquareWebhookSchemaInitializer>();
                services.AddSingleton<ISquareWebhookSchemaInitializer>(new NoOp());
                services.RemoveAll<ISquareTokenSchemaInitializer>();
                services.AddSingleton<ISquareTokenSchemaInitializer>(new NoOp());
                services.RemoveAll<IDeviceActivationCodeService>();
                services.AddSingleton<IDeviceActivationCodeService>(ActivationService);
                services.RemoveAll<IDailyCloseSyncService>();
                services.AddSingleton<IDailyCloseSyncService>(DailyCloseService);
                services.RemoveAll<IDeviceAuthorizationService>();
                services.AddSingleton<IDeviceAuthorizationService>(new TestDeviceAuthorizationService());
                services.RemoveAll<IAuthorizationHandler>();
                services.AddSingleton<IAuthorizationHandler>(new AllowAuthorizationHandler());
            });
        }
    }

    private sealed class RecordingDeviceActivationCodeService : IDeviceActivationCodeService
    {
        public int CallCount { get; private set; }
        public bool LastRecoveryOnly { get; private set; }

        public Task<DeviceActivationCodePreviewResponse> PreviewAsync(
            DeviceActivationCodePreviewRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new DeviceActivationCodePreviewResponse(
                false, DeviceActivationReasonCodes.NotAvailable, null, null, null, null, "Denied"));
        }

        public Task<DeviceActivationCodeRedeemResponse> RedeemAsync(
            DeviceActivationCodeRedeemRequest request,
            bool recoveryOnly,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRecoveryOnly = recoveryOnly;
            return Task.FromResult(Denied());
        }

        public Task<DeviceActivationCodeRedeemResponse> RebindAsync(
            DeviceActivationCodeRebindRequest request,
            DeviceActivationRebindContext currentDevice,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Denied());
        }

        private static DeviceActivationCodeRedeemResponse Denied() =>
            new(string.Empty, string.Empty, string.Empty, 3, false, "Denied", null,
                DeviceActivationReasonCodes.NotAvailable);
    }

    private sealed class RecordingDailyCloseSyncService : IDailyCloseSyncService
    {
        public List<(DailyCloseSyncRequest Request, string StoreCode, string DeviceCode)> Calls { get; } = [];

        public Exception? Failure { get; set; }

        public Task<DailyCloseSyncResponse> SyncAsync(
            DailyCloseSyncRequest request,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken)
        {
            Calls.Add((request, storeCode, deviceCode));
            return Failure is null
                ? Task.FromResult(new DailyCloseSyncResponse(true, false, false))
                : Task.FromException<DailyCloseSyncResponse>(Failure);
        }
    }

    private sealed class TestDeviceAuthorizationService : IDeviceAuthorizationService
    {
        public Task<DeviceAuthorizationValidationResult> ValidateAsync(
            string authorizationCode,
            string deviceCode,
            string storeCode,
            string? hardwareId,
            CancellationToken cancellationToken) =>
            Task.FromResult(DeviceAuthorizationValidationResult.Authorized(
                new DeviceAuthorizationResult(
                    deviceCode,
                    storeCode,
                    hardwareId ?? string.Empty,
                    DeviceSystems.Windows,
                    AllowTransactions: false)));
    }

    private sealed class AllowAuthorizationHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            foreach (var requirement in context.PendingRequirements.ToArray())
            {
                context.Succeed(requirement);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class NoOp : IStoreSchemaInitializer,
        IAttendanceQrKeySchemaInitializer,
        IAdvertisementSchemaInitializer,
        ILinklyCloudCredentialSchemaInitializer,
        IOperationAuditSchemaInitializer,
        IDeviceRuntimeStatusSchemaInitializer,
        ILinklyCloudBackendAsyncSchemaInitializer,
        ILinklySettlementSchemaInitializer,
        IDailyCloseSchemaInitializer,
        IInstallmentRepaymentClaimSchemaInitializer,
        IInstallmentCancelClaimSchemaInitializer,
        ISquareWebhookSchemaInitializer,
        ISquareTokenSchemaInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
