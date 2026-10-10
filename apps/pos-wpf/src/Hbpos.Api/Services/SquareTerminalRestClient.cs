using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Hbpos.Contracts.Square;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public interface ISquareTerminalRestClient
{
    Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(
        string environment,
        string accessToken,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(
        string environment,
        string accessToken,
        string locationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(
        string environment,
        string accessToken,
        string locationId,
        CancellationToken cancellationToken);

    Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(
        string environment,
        string accessToken,
        SquareCreateDeviceCodeRequest request,
        CancellationToken cancellationToken);

    Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(
        string environment,
        string accessToken,
        string deviceCodeId,
        CancellationToken cancellationToken);

    Task<SquareTerminalCheckoutRecord> CreateCheckoutAsync(
        string environment,
        string accessToken,
        SquareCreateCheckoutRequest request,
        CancellationToken cancellationToken);

    Task<SquareTerminalCheckoutRecord?> GetCheckoutAsync(
        string environment,
        string accessToken,
        string checkoutId,
        CancellationToken cancellationToken);

    Task<SquareTerminalCheckoutRecord> CancelCheckoutAsync(
        string environment,
        string accessToken,
        string checkoutId,
        SquareCheckoutActionRequest request,
        CancellationToken cancellationToken);

    Task<SquareTerminalCheckoutRecord> DismissCheckoutAsync(
        string environment,
        string accessToken,
        string checkoutId,
        SquareCheckoutActionRequest request,
        CancellationToken cancellationToken);

    Task<SquarePaymentStatusDto?> GetPaymentAsync(
        string environment,
        string accessToken,
        string paymentId,
        CancellationToken cancellationToken);

    Task<SquareRefundResponse> CreateRefundAsync(
        string environment,
        string accessToken,
        SquareRefundRequest request,
        CancellationToken cancellationToken);

    Task<SquareRefundResponse?> GetRefundAsync(
        string environment,
        string accessToken,
        string refundId,
        CancellationToken cancellationToken);
}

public sealed record SquareTerminalCheckoutRecord(
    string CheckoutId,
    string Environment,
    string? Status = null,
    string? DeviceId = null,
    string? LocationId = null,
    SquareMoneyDto? AmountMoney = null,
    IReadOnlyList<string>? PaymentIds = null,
    string? CancelReason = null,
    DateTimeOffset? UpdatedAt = null);

public sealed class SquareTerminalRestException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class SquareTerminalRestOptions
{
    public const string DefaultApiVersion = "2026-01-22";

    public string? ApiVersion { get; set; } = DefaultApiVersion;

    public string GetApiVersion()
    {
        var apiVersion = string.IsNullOrWhiteSpace(ApiVersion)
            ? DefaultApiVersion
            : ApiVersion.Trim();

        if (!IsValidApiVersion(apiVersion))
        {
            throw new InvalidOperationException("Square:ApiVersion must use yyyy-MM-dd.");
        }

        return apiVersion;
    }

    public static bool IsValidApiVersion(string? apiVersion)
    {
        return string.IsNullOrWhiteSpace(apiVersion) ||
            DateOnly.TryParseExact(
                apiVersion.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
    }
}

public sealed class HttpSquareTerminalRestClient(
    HttpClient httpClient,
    IOptions<SquareTerminalRestOptions>? options = null) : ISquareTerminalRestClient
{
    private const string ProductionBaseUrl = "https://connect.squareup.com/v2/";
    private const string SandboxBaseUrl = "https://connect.squareupsandbox.com/v2/";
    private const string TerminalApiProductType = "TERMINAL_API";
    private static readonly Regex BearerTokenRegex = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex SquareTokenRegex = new(
        @"\b(?:EAAA[A-Za-z0-9._~-]{8,}|sq0(?:atp|csp|idp)-[A-Za-z0-9._~-]{8,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _squareVersion = options?.Value.GetApiVersion() ??
        SquareTerminalRestOptions.DefaultApiVersion;

    public Task<IReadOnlyList<SquareLocationDto>> GetLocationsAsync(
        string environment,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return SendListAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            "locations",
            body: null,
            "locations",
            MapLocation,
            cancellationToken);
    }

    public Task<IReadOnlyList<SquareDeviceDto>> GetDevicesAsync(
        string environment,
        string accessToken,
        string locationId,
        CancellationToken cancellationToken)
    {
        return SendListAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            $"devices?location_id={Uri.EscapeDataString(locationId)}",
            body: null,
            "devices",
            MapDevice,
            cancellationToken);
    }

    public Task<IReadOnlyList<SquareDeviceCodeDto>> GetDeviceCodesAsync(
        string environment,
        string accessToken,
        string locationId,
        CancellationToken cancellationToken)
    {
        return SendListAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            $"devices/codes?location_id={Uri.EscapeDataString(locationId)}&product_type={TerminalApiProductType}",
            body: null,
            "device_codes",
            MapDeviceCode,
            cancellationToken);
    }

    public Task<SquareDeviceCodeDto> CreateDeviceCodeAsync(
        string environment,
        string accessToken,
        SquareCreateDeviceCodeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            idempotency_key = request.IdempotencyKey,
            device_code = new
            {
                // 幂等键由上游显式提供，这里只负责原样透传到 Square。
                name = string.IsNullOrWhiteSpace(request.Name) ? "HBPOS Terminal" : request.Name.Trim(),
                location_id = request.LocationId,
                product_type = TerminalApiProductType
            }
        };

        return SendSingleAsync(
            HttpMethod.Post,
            environment,
            accessToken,
            "devices/codes",
            payload,
            "device_code",
            MapDeviceCode,
            cancellationToken);
    }

    public Task<SquareDeviceCodeDto?> GetDeviceCodeAsync(
        string environment,
        string accessToken,
        string deviceCodeId,
        CancellationToken cancellationToken)
    {
        return SendOptionalAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            $"devices/codes/{Uri.EscapeDataString(deviceCodeId)}",
            body: null,
            "device_code",
            MapDeviceCode,
            cancellationToken);
    }

    public Task<SquareTerminalCheckoutRecord> CreateCheckoutAsync(
        string environment,
        string accessToken,
        SquareCreateCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            idempotency_key = request.IdempotencyKey,
            checkout = new
            {
                amount_money = new
                {
                    amount = request.AmountMoney.Amount,
                    currency = request.AmountMoney.Currency
                },
                device_options = new
                {
                    device_id = request.DeviceId
                },
                location_id = request.LocationId,
                reference_id = request.ReferenceId,
                note = request.Note,
                order_id = request.OrderId
            }
        };

        return SendSingleAsync(
            HttpMethod.Post,
            environment,
            accessToken,
            "terminals/checkouts",
            payload,
            "checkout",
            checkout => MapCheckout(environment, checkout),
            cancellationToken);
    }

    public Task<SquareTerminalCheckoutRecord?> GetCheckoutAsync(
        string environment,
        string accessToken,
        string checkoutId,
        CancellationToken cancellationToken)
    {
        return SendOptionalAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}",
            body: null,
            "checkout",
            checkout => MapCheckout(environment, checkout),
            cancellationToken);
    }

    public Task<SquareTerminalCheckoutRecord> CancelCheckoutAsync(
        string environment,
        string accessToken,
        string checkoutId,
        SquareCheckoutActionRequest request,
        CancellationToken cancellationToken)
    {
        return SendSingleAsync(
            HttpMethod.Post,
            environment,
            accessToken,
            $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}/cancel",
            new { },
            "checkout",
            checkout => MapCheckout(environment, checkout),
            cancellationToken);
    }

    public Task<SquareTerminalCheckoutRecord> DismissCheckoutAsync(
        string environment,
        string accessToken,
        string checkoutId,
        SquareCheckoutActionRequest request,
        CancellationToken cancellationToken)
    {
        return SendSingleAsync(
            HttpMethod.Post,
            environment,
            accessToken,
            $"terminals/checkouts/{Uri.EscapeDataString(checkoutId)}/dismiss",
            new { },
            "checkout",
            checkout => MapCheckout(environment, checkout),
            cancellationToken);
    }

    public Task<SquarePaymentStatusDto?> GetPaymentAsync(
        string environment,
        string accessToken,
        string paymentId,
        CancellationToken cancellationToken)
    {
        return SendOptionalAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            $"payments/{Uri.EscapeDataString(paymentId)}",
            body: null,
            "payment",
            MapPayment,
            cancellationToken);
    }

    public Task<SquareRefundResponse> CreateRefundAsync(
        string environment,
        string accessToken,
        SquareRefundRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            idempotency_key = request.IdempotencyKey,
            payment_id = request.PaymentId,
            amount_money = new
            {
                amount = request.AmountMoney.Amount,
                currency = request.AmountMoney.Currency
            },
            reason = request.Reason
        };

        return SendSingleAsync(
            HttpMethod.Post,
            environment,
            accessToken,
            "refunds",
            payload,
            "refund",
            refund => MapRefund(environment, refund),
            cancellationToken);
    }

    public Task<SquareRefundResponse?> GetRefundAsync(
        string environment,
        string accessToken,
        string refundId,
        CancellationToken cancellationToken)
    {
        return SendOptionalAsync(
            HttpMethod.Get,
            environment,
            accessToken,
            $"refunds/{Uri.EscapeDataString(refundId)}",
            body: null,
            "refund",
            refund => MapRefund(environment, refund),
            cancellationToken);
    }

    private async Task<IReadOnlyList<T>> SendListAsync<T>(
        HttpMethod method,
        string environment,
        string accessToken,
        string relativeUrl,
        object? body,
        string rootProperty,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken)
    {
        var root = await SendForRootAsync(
            method,
            environment,
            accessToken,
            relativeUrl,
            body,
            rootProperty,
            cancellationToken);
        if (root is null || root.Value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<T>();
        foreach (var item in root.Value.EnumerateArray())
        {
            list.Add(map(item));
        }

        return list;
    }

    private async Task<T> SendSingleAsync<T>(
        HttpMethod method,
        string environment,
        string accessToken,
        string relativeUrl,
        object? body,
        string rootProperty,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken)
    {
        var root = await SendForRootAsync(
            method,
            environment,
            accessToken,
            relativeUrl,
            body,
            rootProperty,
            cancellationToken);
        if (root is null || root.Value.ValueKind == JsonValueKind.Null)
        {
            throw new SquareTerminalRestException(
                HttpStatusCode.BadGateway,
                $"Square REST response is missing '{rootProperty}'.");
        }

        return map(root.Value);
    }

    private async Task<T?> SendOptionalAsync<T>(
        HttpMethod method,
        string environment,
        string accessToken,
        string relativeUrl,
        object? body,
        string rootProperty,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        var root = await SendForRootAsync(
            method,
            environment,
            accessToken,
            relativeUrl,
            body,
            rootProperty,
            cancellationToken);
        if (root is null || root.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return map(root.Value);
    }

    private async Task<JsonElement?> SendForRootAsync(
        HttpMethod method,
        string environment,
        string accessToken,
        string relativeUrl,
        object? body,
        string rootProperty,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(method, environment, accessToken, relativeUrl, body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateRestException(response.StatusCode, content, accessToken);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty(rootProperty, out var value))
        {
            return null;
        }

        return value.Clone();
    }

    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        string environment,
        string accessToken,
        string relativeUrl,
        object? body)
    {
        var request = new HttpRequestMessage(method, new Uri(GetBaseUri(environment), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Square-Version", _squareVersion);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return request;
    }

    private static Uri GetBaseUri(string environment)
    {
        return SquareTokenService.NormalizeEnvironment(environment) switch
        {
            "Production" => new Uri(ProductionBaseUrl),
            "Sandbox" => new Uri(SandboxBaseUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unsupported Square environment.")
        };
    }

    private static SquareTerminalRestException CreateRestException(
        HttpStatusCode statusCode,
        string content,
        string accessToken)
    {
        var message = $"Square REST request failed with HTTP {(int)statusCode}.";
        if (string.IsNullOrWhiteSpace(content))
        {
            return new SquareTerminalRestException(statusCode, message);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                var details = new List<string>();
                foreach (var error in errors.EnumerateArray())
                {
                    var code = TryGetString(error, "code");
                    var detail = TryGetString(error, "detail");
                    var combined = string.Join(
                        ": ",
                        new[] { code, detail }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    if (!string.IsNullOrWhiteSpace(combined))
                    {
                        details.Add(combined);
                    }
                }

                if (details.Count > 0)
                {
                    return new SquareTerminalRestException(
                        statusCode,
                        Sanitize($"{message} {string.Join(" | ", details)}", accessToken));
                }
            }
        }
        catch (JsonException)
        {
            // 保留兜底消息即可，避免为了错误解析再引入新的异常。
        }

        return new SquareTerminalRestException(
            statusCode,
            Sanitize($"{message} {content}", accessToken));
    }

    private static string Sanitize(string value, string accessToken)
    {
        var sanitized = string.IsNullOrEmpty(accessToken)
            ? value
            : value.Replace(accessToken, "[REDACTED]", StringComparison.Ordinal);

        // Square 正常不会回显 token；这里保留兜底，防止上游错误消息带出 Bearer 或 Square token 形态。
        sanitized = BearerTokenRegex.Replace(sanitized, "Bearer [REDACTED]");
        return SquareTokenRegex.Replace(sanitized, "[REDACTED]");
    }

    private static SquareLocationDto MapLocation(JsonElement element)
    {
        return new SquareLocationDto(
            TryGetString(element, "id") ?? string.Empty,
            TryGetString(element, "name") ?? string.Empty,
            Status: TryGetString(element, "status"),
            Currency: TryGetString(element, "currency"),
            Country: TryGetString(element, "country"));
    }

    private static SquareDeviceDto MapDevice(JsonElement element)
    {
        return new SquareDeviceDto(
            TryGetString(element, "id") ?? string.Empty,
            Code: TryGetString(element, "code"),
            // Square Device 的显示名在 attributes.name；保留顶层 name 只作为旧响应兼容。
            Name: TryGetNestedString(element, "attributes", "name") ?? TryGetString(element, "name"),
            Status: TryGetNestedString(element, "status", "category") ?? TryGetString(element, "status"),
            LocationId: TryGetString(element, "location_id") ?? TryGetTerminalApplicationLocation(element));
    }

    private static SquareDeviceCodeDto MapDeviceCode(JsonElement element)
    {
        return new SquareDeviceCodeDto(
            TryGetString(element, "id") ?? string.Empty,
            Code: TryGetString(element, "code"),
            Status: TryGetString(element, "status"),
            DeviceId: TryGetString(element, "device_id"),
            LocationId: TryGetString(element, "location_id"),
            Name: TryGetString(element, "name"));
    }

    private static SquareTerminalCheckoutRecord MapCheckout(string environment, JsonElement element)
    {
        return new SquareTerminalCheckoutRecord(
            TryGetString(element, "id") ?? string.Empty,
            SquareTokenService.NormalizeEnvironment(environment) ?? environment,
            Status: TryGetString(element, "status"),
            // Square 官方 checkout 将设备号放在 device_options.device_id；顶层 device_id 只作为旧响应兜底。
            DeviceId: TryGetNestedString(element, "device_options", "device_id") ?? TryGetString(element, "device_id"),
            LocationId: TryGetString(element, "location_id"),
            AmountMoney: TryGetMoney(element, "amount_money"),
            PaymentIds: TryGetStringList(element, "payment_ids"),
            CancelReason: TryGetString(element, "cancel_reason"),
            UpdatedAt: TryGetDateTimeOffset(element, "updated_at"));
    }

    private static SquarePaymentStatusDto MapPayment(JsonElement element)
    {
        return new SquarePaymentStatusDto(
            TryGetString(element, "id") ?? string.Empty,
            Status: TryGetString(element, "status"),
            ApprovedMoney: TryGetMoney(element, "approved_money"),
            TotalMoney: TryGetMoney(element, "total_money"),
            UpdatedAt: TryGetDateTimeOffset(element, "updated_at"),
            CardBrand: NormalizeOptionalText(TryGetNestedString(element, "card_details", "card", "card_brand")),
            // 只从 Square 的 last_4 生成脱敏显示值，避免把 BIN、有效期或 fingerprint 带给前端。
            MaskedCardNumber: FormatMaskedCardNumber(TryGetNestedString(element, "card_details", "card", "last_4")),
            AuthCode: NormalizeOptionalText(TryGetNestedString(element, "card_details", "auth_result_code")),
            // 退款请求结果丢失时，POS 靠付款上的 refund_ids 找回已受理的退款。
            RefundIds: TryGetStringList(element, "refund_ids"));
    }

    private static SquareRefundResponse MapRefund(string environment, JsonElement element)
    {
        return new SquareRefundResponse(
            TryGetString(element, "id") ?? string.Empty,
            SquareTokenService.NormalizeEnvironment(environment) ?? environment,
            Status: TryGetString(element, "status"),
            PaymentId: TryGetString(element, "payment_id"),
            AmountMoney: TryGetMoney(element, "amount_money"),
            UpdatedAt: TryGetDateTimeOffset(element, "updated_at"),
            CreatedAt: TryGetDateTimeOffset(element, "created_at"));
    }

    private static SquareMoneyDto? TryGetMoney(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var moneyElement) ||
            moneyElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!moneyElement.TryGetProperty("amount", out var amountElement) ||
            amountElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return new SquareMoneyDto(
            amountElement.GetInt64(),
            TryGetString(moneyElement, "currency") ?? string.Empty);
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var dateTimeOffset)
            ? dateTimeOffset
            : null;
    }

    private static IReadOnlyList<string> TryGetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string? TryGetNestedString(JsonElement element, params string[] propertyPath)
    {
        var current = element;
        foreach (var propertyName in propertyPath)
        {
            if (!current.TryGetProperty(propertyName, out current) ||
                current.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FormatMaskedCardNumber(string? last4)
    {
        var normalized = NormalizeOptionalText(last4);
        if (normalized is null)
        {
            return null;
        }

        var digits = new string(normalized.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        var lastFour = digits.Length > 4 ? digits[^4..] : digits;
        return $"****{lastFour}";
    }

    private static string? TryGetTerminalApplicationLocation(JsonElement element)
    {
        if (!element.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var component in components.EnumerateArray())
        {
            var applicationType = TryGetNestedString(component, "application_details", "application_type");
            if (!string.Equals(applicationType, TerminalApiProductType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sessionLocation = TryGetNestedString(component, "application_details", "session_location");
            if (!string.IsNullOrWhiteSpace(sessionLocation))
            {
                return sessionLocation;
            }
        }

        return null;
    }
}
