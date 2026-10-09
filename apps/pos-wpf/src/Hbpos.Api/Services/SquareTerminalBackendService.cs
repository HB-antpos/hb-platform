using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Hbpos.Contracts.Square;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public sealed class SquareTerminalBackendException(
    string code,
    string message,
    HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable) : Exception(message)
{
    public string Code { get; } = code;

    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class SquareTerminalBackendService(
    ISquareTokenService squareTokenService,
    ISquareTerminalRestClient restClient,
    ISquareWebhookVerifier? webhookVerifier = null,
    IOptions<SquareWebhookOptions>? webhookOptions = null,
    ISquareCheckoutSessionRepository? checkoutSessionRepository = null,
    ISquareRefundRepository? refundRepository = null) : ISquareTerminalBackendService
{
    private const string TokenNotConfiguredCode = "SQUARE_TOKEN_NOT_CONFIGURED";
    private const string TokenNotConfiguredMessage = "Square token is not configured for this environment.";
    private const string IdempotencyKeyRequiredCode = "SQUARE_IDEMPOTENCY_KEY_REQUIRED";
    private const string IdempotencyKeyRequiredMessage = "idempotencyKey is required.";
    private const string WebhookEnvironmentInvalidCode = "SQUARE_WEBHOOK_ENVIRONMENT_INVALID";
    private const string WebhookEnvironmentInvalidMessage = "Square webhook environment must be Production or Sandbox.";
    private const string WebhookSignatureKeyNotConfiguredCode = "SQUARE_WEBHOOK_SIGNATURE_KEY_NOT_CONFIGURED";
    private const string WebhookSignatureKeyNotConfiguredMessage = "Square webhook signature key is not configured for this environment.";
    private const string WebhookSignatureInvalidCode = "SQUARE_WEBHOOK_SIGNATURE_INVALID";
    private const string WebhookSignatureInvalidMessage = "Square webhook signature is invalid.";
    private const string WebhookPayloadInvalidCode = "SQUARE_WEBHOOK_PAYLOAD_INVALID";
    private const string WebhookPayloadInvalidMessage = "Square webhook payload is invalid.";

    private readonly ISquareWebhookVerifier _webhookVerifier = webhookVerifier ?? new SquareWebhookVerifier();
    private readonly SquareWebhookOptions _webhookOptions = (webhookOptions ?? Options.Create(new SquareWebhookOptions())).Value;
    private readonly ISquareCheckoutSessionRepository _checkoutSessionRepository = checkoutSessionRepository ?? new NullSquareCheckoutSessionRepository();
    private readonly ISquareRefundRepository _refundRepository = refundRepository ?? new NullSquareRefundRepository();

    public async Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(environment, cancellationToken);
        return await restClient.GetLocationsAsync(context.Environment, context.AccessToken, cancellationToken);
    }

    public async Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(
        string environment,
        string locationId,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(environment, cancellationToken);
        return await restClient.GetDevicesAsync(context.Environment, context.AccessToken, locationId, cancellationToken);
    }

    public async Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(
        string environment,
        string locationId,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(environment, cancellationToken);
        return await restClient.GetDeviceCodesAsync(context.Environment, context.AccessToken, locationId, cancellationToken);
    }

    public async Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(
        SquareCreateDeviceCodeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var context = await GetRequestContextAsync(request.Environment, cancellationToken);
        return await restClient.CreateDeviceCodeAsync(context.Environment, context.AccessToken, request, cancellationToken);
    }

    public async Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(
        string environment,
        string deviceCodeId,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(environment, cancellationToken);
        return await restClient.GetDeviceCodeAsync(context.Environment, context.AccessToken, deviceCodeId, cancellationToken);
    }

    public async Task<SquareCheckoutStatusResponse> CreateCheckoutAsync(
        SquareCreateCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var context = await GetRequestContextAsync(request.Environment, cancellationToken);
        var checkoutRequest = ResolveCheckoutRequest(context.Environment, request);
        var checkout = await restClient.CreateCheckoutAsync(context.Environment, context.AccessToken, checkoutRequest, cancellationToken);
        return MapCheckout(checkout);
    }

    public async Task<SquareCheckoutStatusResponse?> GetCheckoutAsync(
        string environment,
        string checkoutId,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(environment, cancellationToken);
        SquareTerminalCheckoutRecord? checkout;
        Exception? checkoutLookupException = null;
        try
        {
            checkout = await restClient.GetCheckoutAsync(context.Environment, context.AccessToken, checkoutId, cancellationToken);
        }
        catch (SquareTerminalRestException ex)
        {
            checkoutLookupException = ex;
            checkout = null;
        }
        catch (JsonException ex)
        {
            checkoutLookupException = ex;
            checkout = null;
        }

        if (checkout is null)
        {
            checkout = await GetWebhookCheckoutAsync(context.Environment, checkoutId, cancellationToken);
            if (checkout is null)
            {
                if (checkoutLookupException is not null)
                {
                    ExceptionDispatchInfo.Capture(checkoutLookupException).Throw();
                }

                return null;
            }
        }

        SquarePaymentStatusDto? payment = null;
        // 只有 Square 已经明确完成 checkout 且给出 payment id，才追补 payments 明细。
        if (string.Equals(checkout.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var paymentId = checkout.PaymentIds?.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            if (!string.IsNullOrWhiteSpace(paymentId))
            {
                payment = await restClient.GetPaymentAsync(
                    context.Environment,
                    context.AccessToken,
                    paymentId,
                    cancellationToken);
            }
        }

        return MapCheckout(checkout, payment);
    }

    public async Task<SquarePaymentStatusDto?> GetPaymentAsync(
        string environment,
        string paymentId,
        CancellationToken cancellationToken)
    {
        // payment 查询也统一复用后端 token 读取，避免任何终端侧 token 直连调用。
        var context = await GetRequestContextAsync(environment, cancellationToken);
        return await restClient.GetPaymentAsync(context.Environment, context.AccessToken, paymentId, cancellationToken);
    }

    public async Task<SquareCheckoutStatusResponse> CancelCheckoutAsync(
        string checkoutId,
        SquareCheckoutActionRequest request,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(request.Environment, cancellationToken);
        var checkout = await restClient.CancelCheckoutAsync(
            context.Environment,
            context.AccessToken,
            checkoutId,
            request,
            cancellationToken);
        return MapCheckout(checkout);
    }

    public async Task<SquareCheckoutStatusResponse> DismissCheckoutAsync(
        string checkoutId,
        SquareCheckoutActionRequest request,
        CancellationToken cancellationToken)
    {
        var context = await GetRequestContextAsync(request.Environment, cancellationToken);
        var checkout = await restClient.DismissCheckoutAsync(
            context.Environment,
            context.AccessToken,
            checkoutId,
            request,
            cancellationToken);
        return MapCheckout(checkout);
    }

    public async Task<SquareRefundResponse> CreateRefundAsync(
        SquareRefundRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var context = await GetRequestContextAsync(request.Environment, cancellationToken);
        return await restClient.CreateRefundAsync(context.Environment, context.AccessToken, request, cancellationToken);
    }

    public async Task<SquareRefundResponse?> GetRefundAsync(
        string environment,
        string refundId,
        CancellationToken cancellationToken)
    {
        // 退款终态查询统一由后端持有 Square token，POS 不直连 Square。
        var context = await GetRequestContextAsync(environment, cancellationToken);
        try
        {
            return await restClient.GetRefundAsync(
                context.Environment,
                context.AccessToken,
                refundId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is SquareTerminalRestException or JsonException)
        {
            // Square 实时查询失败时，用 refund.updated 通知落库的最近状态兜底；没有通知记录就保持原错误。
            var stored = await _refundRepository.GetRefundAsync(context.Environment, refundId, cancellationToken);
            if (stored is null)
            {
                throw;
            }

            return MapStoredRefund(stored);
        }
    }

    public Task<SquareWebhookAcceptedResponse> AcceptWebhookAsync(
        SquareWebhookRequest request,
        CancellationToken cancellationToken)
    {
        return AcceptWebhookCoreAsync(request, cancellationToken);
    }

    private async Task<SquareTerminalRequestContext> GetRequestContextAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = SquareTokenService.NormalizeEnvironment(environment) ?? environment.Trim();
        var token = await squareTokenService.GetActiveTokenAsync(normalizedEnvironment, cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new SquareTerminalBackendException(TokenNotConfiguredCode, TokenNotConfiguredMessage);
        }

        return new SquareTerminalRequestContext(normalizedEnvironment, token.AccessToken);
    }

    private static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // 设备码重试需要沿用同一个幂等键，缺失时直接返回稳定错误，不能在后端悄悄改写。
            throw new SquareTerminalBackendException(
                IdempotencyKeyRequiredCode,
                IdempotencyKeyRequiredMessage,
                HttpStatusCode.BadRequest);
        }
    }

    private static SquareCreateCheckoutRequest ResolveCheckoutRequest(
        string environment,
        SquareCreateCheckoutRequest request)
    {
        if (!string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return request;
        }

        // Sandbox Terminal checkout 只能靠 Square 官方特殊 device_id 控制模拟结果；普通配对设备 ID 在这里兜底为成功卡测值。
        var sandboxDeviceId = SquareSandboxTerminalDeviceIds.ResolveCheckoutDeviceId(request.DeviceId);
        return string.Equals(request.DeviceId, sandboxDeviceId, StringComparison.OrdinalIgnoreCase)
            ? request
            : request with { DeviceId = sandboxDeviceId };
    }

    private static SquareCheckoutStatusResponse MapCheckout(
        SquareTerminalCheckoutRecord checkout,
        SquarePaymentStatusDto? payment = null)
    {
        return new SquareCheckoutStatusResponse(
            checkout.CheckoutId,
            checkout.Environment,
            Status: checkout.Status,
            DeviceId: checkout.DeviceId,
            LocationId: checkout.LocationId,
            AmountMoney: checkout.AmountMoney,
            Payment: payment,
            PaymentIds: checkout.PaymentIds,
            CancelReason: checkout.CancelReason,
            UpdatedAt: checkout.UpdatedAt);
    }

    private async Task<SquareTerminalCheckoutRecord?> GetWebhookCheckoutAsync(
        string environment,
        string checkoutId,
        CancellationToken cancellationToken)
    {
        var session = await _checkoutSessionRepository.GetCheckoutSessionAsync(environment, checkoutId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        // webhook 只作为终端 checkout 状态兜底；付款成功仍必须走官方 payment 查询验证。
        return MapWebhookCheckoutSession(session);
    }

    private static SquareTerminalCheckoutRecord MapWebhookCheckoutSession(SquareCheckoutSessionRecord session)
    {
        var status = session.Status;
        var amount = session.Amount;
        var currency = session.Currency;
        var deviceId = session.DeviceId;
        var locationId = session.LocationId;
        var updatedAt = session.UpdatedAt;
        string? cancelReason = null;
        IReadOnlyList<string> paymentIds = ReadPaymentIdsJson(session.PaymentIdsJson);
        if (paymentIds.Count == 0 && !string.IsNullOrWhiteSpace(session.PaymentId))
        {
            paymentIds = [session.PaymentId.Trim()];
        }

        if (!string.IsNullOrWhiteSpace(session.RawCheckoutJson))
        {
            try
            {
                using var document = JsonDocument.Parse(session.RawCheckoutJson);
                var checkoutElement = document.RootElement;
                status = TryReadString(checkoutElement, "status") ?? status;
                cancelReason = TryReadString(checkoutElement, "cancel_reason");
                deviceId = TryReadNestedString(checkoutElement, "device_options", "device_id") ??
                    TryReadString(checkoutElement, "device_id") ??
                    deviceId;
                locationId = TryReadString(checkoutElement, "location_id") ?? locationId;
                updatedAt = TryReadDateTimeOffset(checkoutElement, "updated_at") ?? updatedAt;

                if (TryGetProperty(checkoutElement, "amount_money", out var amountElement))
                {
                    var rawAmount = ReadAmount(amountElement);
                    amount = rawAmount.Amount ?? amount;
                    currency = rawAmount.Currency ?? currency;
                }

                var rawPaymentIds = ReadPaymentIds(checkoutElement);
                if (rawPaymentIds.Count > 0)
                {
                    paymentIds = rawPaymentIds;
                }
            }
            catch (JsonException)
            {
                // webhook 原文只用于补充字段；解析失败时仍保留已落库的结构化状态。
            }
        }

        return new SquareTerminalCheckoutRecord(
            session.CheckoutId,
            session.Environment,
            Status: status,
            DeviceId: deviceId,
            LocationId: locationId,
            AmountMoney: amount.HasValue ? new SquareMoneyDto(amount.Value, currency ?? string.Empty) : null,
            PaymentIds: paymentIds,
            CancelReason: cancelReason,
            UpdatedAt: updatedAt);
    }

    private sealed record SquareTerminalRequestContext(
        string Environment,
        string AccessToken);

    private async Task<SquareWebhookAcceptedResponse> AcceptWebhookCoreAsync(
        SquareWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var environment = ValidateWebhookSignatureAndResolveEnvironment(request);

        var envelope = ParseWebhookEnvelope(request.RawBody, environment);
        var inserted = await _checkoutSessionRepository.TryAddWebhookEventAsync(
            new SquareWebhookEventRecord
            {
                Environment = environment,
                EventId = envelope.EventId,
                EventType = envelope.EventType,
                PayloadJson = request.RawBody,
                ReceivedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
        if (!inserted)
        {
            return new SquareWebhookAcceptedResponse(
                "deduplicated",
                envelope.EventId,
                "Duplicate webhook event ignored.");
        }

        if (envelope.Refund is not null)
        {
            // 退款通知只落库为状态兜底；POS 仍以 GET refunds 的结果为准，并核对退款号/金额/原付款。
            var refundUpdatedAt = envelope.Refund.UpdatedAt ?? envelope.CreatedAt ?? DateTimeOffset.UtcNow;
            await _refundRepository.UpsertRefundAsync(
                new SquareRefundRecord
                {
                    Environment = environment,
                    RefundId = envelope.Refund.RefundId,
                    Status = envelope.Refund.Status,
                    PaymentId = envelope.Refund.PaymentId,
                    Amount = envelope.Refund.Amount,
                    Currency = envelope.Refund.Currency,
                    RawRefundJson = envelope.Refund.RawRefundJson,
                    LastEventId = envelope.EventId,
                    UpdatedAt = refundUpdatedAt
                },
                cancellationToken);
        }

        if (string.Equals(envelope.EventType, "terminal.checkout.updated", StringComparison.OrdinalIgnoreCase))
        {
            if (envelope.Checkout is null)
            {
                throw new SquareTerminalBackendException(
                    WebhookPayloadInvalidCode,
                    WebhookPayloadInvalidMessage,
                    HttpStatusCode.BadRequest);
            }

            // 事件时间优先取 webhook created_at，其次回退 checkout.updated_at；repository 再负责时间比较和终态保护。
            var sessionUpdatedAt = envelope.CreatedAt ?? envelope.Checkout.UpdatedAt ?? DateTimeOffset.UtcNow;
            await _checkoutSessionRepository.UpsertCheckoutSessionAsync(
                new SquareCheckoutSessionRecord
                {
                    Environment = environment,
                    CheckoutId = envelope.Checkout.CheckoutId,
                    Status = envelope.Checkout.Status,
                    Amount = envelope.Checkout.Amount,
                    Currency = envelope.Checkout.Currency,
                    DeviceId = envelope.Checkout.DeviceId,
                    LocationId = envelope.Checkout.LocationId,
                    PaymentId = envelope.Checkout.PaymentId,
                    PaymentIdsJson = envelope.Checkout.PaymentIdsJson,
                    RawCheckoutJson = envelope.Checkout.RawCheckoutJson,
                    LastEventId = envelope.EventId,
                    UpdatedAt = sessionUpdatedAt
                },
                cancellationToken);
        }

        return new SquareWebhookAcceptedResponse(
            "accepted",
            envelope.EventId,
            "Webhook accepted.");
    }

    private string ValidateWebhookSignatureAndResolveEnvironment(SquareWebhookRequest request)
    {
        var requestedEnvironment = SquareTokenService.NormalizeEnvironment(request.SquareEnvironmentHeader);
        var candidates = requestedEnvironment is null
            ? GetConfiguredWebhookEnvironments()
            : [requestedEnvironment];

        var hasConfiguredSignatureKey = false;
        foreach (var environment in candidates)
        {
            var signatureKey = _webhookOptions.GetSignatureKey(environment);
            if (string.IsNullOrWhiteSpace(signatureKey))
            {
                continue;
            }

            hasConfiguredSignatureKey = true;
            // Square 官方投递没有环境 header；缺失时按已配置环境逐个验签，验中过的环境即为事件归属。
            var notificationUrl = _webhookOptions.GetNotificationUrl(environment) ?? request.NotificationUrl;
            if (_webhookVerifier.IsValid(signatureKey, notificationUrl, request.RawBody, request.SignatureHeader))
            {
                return environment;
            }
        }

        if (requestedEnvironment is null && !string.IsNullOrWhiteSpace(request.SquareEnvironmentHeader))
        {
            throw new SquareTerminalBackendException(
                WebhookEnvironmentInvalidCode,
                WebhookEnvironmentInvalidMessage,
                HttpStatusCode.BadRequest);
        }

        throw new SquareTerminalBackendException(
            hasConfiguredSignatureKey ? WebhookSignatureInvalidCode : WebhookSignatureKeyNotConfiguredCode,
            hasConfiguredSignatureKey ? WebhookSignatureInvalidMessage : WebhookSignatureKeyNotConfiguredMessage,
            HttpStatusCode.Forbidden);
    }

    private IEnumerable<string> GetConfiguredWebhookEnvironments()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var environment in _webhookOptions.WebhookSignatureKeys.Keys)
        {
            var normalized = SquareTokenService.NormalizeEnvironment(environment);
            if (normalized is not null && seen.Add(normalized))
            {
                yield return normalized;
            }
        }

        if (!string.IsNullOrWhiteSpace(_webhookOptions.WebhookSignatureKey))
        {
            foreach (var environment in new[] { "Production", "Sandbox" })
            {
                if (seen.Add(environment))
                {
                    yield return environment;
                }
            }
        }
    }

    private static SquareWebhookEnvelope ParseWebhookEnvelope(string rawBody, string environment)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var eventId = ReadRequiredString(root, "event_id");
            var eventType = ReadRequiredString(root, "type");
            var createdAt = TryReadDateTimeOffset(root, "created_at");
            SquareWebhookCheckoutEnvelope? checkout = null;
            if (string.Equals(eventType, "terminal.checkout.updated", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetNestedElement(root, out var checkoutElement, "data", "object", "checkout"))
                {
                    throw CreateInvalidWebhookPayloadException();
                }

                checkout = ParseCheckout(checkoutElement);
            }

            SquareWebhookRefundEnvelope? refund = null;
            if (string.Equals(eventType, "refund.updated", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventType, "refund.created", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetNestedElement(root, out var refundElement, "data", "object", "refund"))
                {
                    throw CreateInvalidWebhookPayloadException();
                }

                refund = ParseRefund(refundElement);
            }

            return new SquareWebhookEnvelope(environment, eventId, eventType, createdAt, checkout, refund);
        }
        catch (JsonException)
        {
            throw CreateInvalidWebhookPayloadException();
        }
    }

    private static SquareWebhookRefundEnvelope ParseRefund(JsonElement refundElement)
    {
        var amountMoney = TryGetProperty(refundElement, "amount_money", out var amountElement)
            ? ReadAmount(amountElement)
            : (Amount: (long?)null, Currency: (string?)null);
        return new SquareWebhookRefundEnvelope(
            ReadRequiredString(refundElement, "id"),
            ReadRequiredString(refundElement, "status").ToUpperInvariant(),
            TryReadString(refundElement, "payment_id"),
            amountMoney.Amount,
            amountMoney.Currency,
            TryReadDateTimeOffset(refundElement, "updated_at"),
            refundElement.GetRawText());
    }

    private static SquareRefundResponse MapStoredRefund(SquareRefundRecord stored)
    {
        return new SquareRefundResponse(
            stored.RefundId,
            stored.Environment,
            Status: stored.Status,
            PaymentId: stored.PaymentId,
            AmountMoney: stored.Amount.HasValue ? new SquareMoneyDto(stored.Amount.Value, stored.Currency ?? string.Empty) : null,
            UpdatedAt: stored.UpdatedAt);
    }

    private static SquareWebhookCheckoutEnvelope ParseCheckout(JsonElement checkoutElement)
    {
        var checkoutId = ReadRequiredString(checkoutElement, "id");
        var status = ReadRequiredString(checkoutElement, "status");
        var amountMoney = TryGetProperty(checkoutElement, "amount_money", out var amountElement)
            ? ReadAmount(amountElement)
            : (Amount: (long?)null, Currency: (string?)null);
        var deviceId = TryReadNestedString(checkoutElement, "device_options", "device_id");
        var locationId = TryReadString(checkoutElement, "location_id");
        var updatedAt = TryReadDateTimeOffset(checkoutElement, "updated_at");
        var paymentIds = ReadPaymentIds(checkoutElement);
        return new SquareWebhookCheckoutEnvelope(
            checkoutId,
            status,
            amountMoney.Amount,
            amountMoney.Currency,
            deviceId,
            locationId,
            updatedAt,
            paymentIds.FirstOrDefault(),
            paymentIds.Count == 0 ? null : JsonSerializer.Serialize(paymentIds),
            checkoutElement.GetRawText());
    }

    private static (long? Amount, string? Currency) ReadAmount(JsonElement amountElement)
    {
        long? amount = null;
        if (TryGetProperty(amountElement, "amount", out var amountValue) &&
            amountValue.ValueKind == JsonValueKind.Number &&
            amountValue.TryGetInt64(out var parsedAmount))
        {
            amount = parsedAmount;
        }

        var currency = TryReadString(amountElement, "currency");
        return (amount, currency);
    }

    private static List<string> ReadPaymentIds(JsonElement checkoutElement)
    {
        var paymentIds = new List<string>();
        if (!TryGetProperty(checkoutElement, "payment_ids", out var paymentIdsElement) ||
            paymentIdsElement.ValueKind != JsonValueKind.Array)
        {
            return paymentIds;
        }

        foreach (var paymentIdElement in paymentIdsElement.EnumerateArray())
        {
            if (paymentIdElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var paymentId = paymentIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(paymentId))
            {
                paymentIds.Add(paymentId.Trim());
            }
        }

        return paymentIds;
    }

    private static IReadOnlyList<string> ReadPaymentIdsJson(string? paymentIdsJson)
    {
        if (string.IsNullOrWhiteSpace(paymentIdsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(paymentIdsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var paymentIds = new List<string>();
            foreach (var paymentIdElement in document.RootElement.EnumerateArray())
            {
                if (paymentIdElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var paymentId = paymentIdElement.GetString();
                if (!string.IsNullOrWhiteSpace(paymentId))
                {
                    paymentIds.Add(paymentId.Trim());
                }
            }

            return paymentIds;
        }
        catch (JsonException)
        {
            // 落库 JSON 若损坏，不影响 checkout 状态 fallback；payment id 会回退到结构化字段。
            return [];
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = TryReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw CreateInvalidWebhookPayloadException();
        }

        return value;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    private static string? TryReadNestedString(JsonElement element, params string[] propertyPath)
    {
        return TryGetNestedElement(element, out var nestedElement, propertyPath) && nestedElement.ValueKind == JsonValueKind.String
            ? nestedElement.GetString()?.Trim()
            : null;
    }

    private static DateTimeOffset? TryReadDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = TryReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool TryGetNestedElement(
        JsonElement element,
        out JsonElement current,
        params string[] propertyPath)
    {
        current = element;
        foreach (var propertyName in propertyPath)
        {
            if (!TryGetProperty(current, propertyName, out current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static SquareTerminalBackendException CreateInvalidWebhookPayloadException()
    {
        return new SquareTerminalBackendException(
            WebhookPayloadInvalidCode,
            WebhookPayloadInvalidMessage,
            HttpStatusCode.BadRequest);
    }

    private sealed record SquareWebhookEnvelope(
        string Environment,
        string EventId,
        string EventType,
        DateTimeOffset? CreatedAt,
        SquareWebhookCheckoutEnvelope? Checkout,
        SquareWebhookRefundEnvelope? Refund = null);

    private sealed record SquareWebhookRefundEnvelope(
        string RefundId,
        string Status,
        string? PaymentId,
        long? Amount,
        string? Currency,
        DateTimeOffset? UpdatedAt,
        string RawRefundJson);

    private sealed record SquareWebhookCheckoutEnvelope(
        string CheckoutId,
        string Status,
        long? Amount,
        string? Currency,
        string? DeviceId,
        string? LocationId,
        DateTimeOffset? UpdatedAt,
        string? PaymentId,
        string? PaymentIdsJson,
        string RawCheckoutJson);

    private sealed class NullSquareRefundRepository : ISquareRefundRepository
    {
        public Task UpsertRefundAsync(SquareRefundRecord refund, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SquareRefundRecord?> GetRefundAsync(
            string environment,
            string refundId,
            CancellationToken cancellationToken) => Task.FromResult<SquareRefundRecord?>(null);
    }

    private sealed class NullSquareCheckoutSessionRepository : ISquareCheckoutSessionRepository
    {
        public Task<SquareCheckoutSessionRecord?> BindCheckoutOriginAsync(
            string environment,
            string checkoutId,
            string originStoreCode,
            string originDeviceCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<SquareCheckoutSessionRecord?>(null);
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionAsync(
            string environment,
            string checkoutId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<SquareCheckoutSessionRecord?>(null);
        }

        public Task<SquareCheckoutSessionRecord?> GetCheckoutSessionByPaymentIdAsync(
            string environment,
            string paymentId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<SquareCheckoutSessionRecord?>(null);
        }

        public Task<bool> TryAddWebhookEventAsync(
            SquareWebhookEventRecord webhookEvent,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task UpsertCheckoutSessionAsync(
            SquareCheckoutSessionRecord session,
            CancellationToken cancellationToken)
        {
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
    }
}
