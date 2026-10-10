namespace Hbpos.Contracts.Square;

public sealed record SquareLocationDto(
    string Id,
    string Name,
    string? Status = null,
    string? Currency = null,
    string? Country = null);

public sealed record SquareDeviceDto(
    string Id,
    string? Code = null,
    string? Name = null,
    string? Status = null,
    string? LocationId = null);

public sealed record SquareDeviceCodeDto(
    string Id,
    string? Code = null,
    string? Status = null,
    string? DeviceId = null,
    string? LocationId = null,
    string? Name = null);

public sealed record SquareCreateDeviceCodeRequest(
    string Environment,
    string IdempotencyKey,
    string LocationId,
    string? Name = null,
    string? ProductType = null);

public sealed record SquareMoneyDto(
    long Amount,
    string Currency);

public sealed record SquareCreateCheckoutRequest(
    string Environment,
    string IdempotencyKey,
    string DeviceId,
    string LocationId,
    SquareMoneyDto AmountMoney,
    string? ReferenceId = null,
    string? Note = null,
    string? OrderId = null);

public sealed record SquareCheckoutActionRequest(
    string Environment,
    string? Reason = null);

public sealed record SquarePaymentStatusDto(
    string PaymentId,
    string? Status = null,
    SquareMoneyDto? ApprovedMoney = null,
    SquareMoneyDto? TotalMoney = null,
    DateTimeOffset? UpdatedAt = null,
    string? CardBrand = null,
    string? MaskedCardNumber = null,
    string? AuthCode = null,
    IReadOnlyList<string>? RefundIds = null);

public sealed record SquareCheckoutStatusResponse(
    string CheckoutId,
    string Environment,
    string? Status = null,
    string? DeviceId = null,
    string? LocationId = null,
    SquareMoneyDto? AmountMoney = null,
    SquarePaymentStatusDto? Payment = null,
    IReadOnlyList<string>? PaymentIds = null,
    string? CancelReason = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record SquareRefundRequest(
    string Environment,
    string IdempotencyKey,
    string PaymentId,
    SquareMoneyDto AmountMoney,
    string? Reason = null);

public sealed record SquareRefundResponse(
    string RefundId,
    string Environment,
    string? Status = null,
    string? PaymentId = null,
    SquareMoneyDto? AmountMoney = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? CreatedAt = null);

public sealed record SquareWebhookRequest(
    string RawBody,
    string? SignatureHeader,
    string? SquareEnvironmentHeader,
    string NotificationUrl);

public sealed record SquareWebhookAcceptedResponse(
    string Status,
    string? EventId = null,
    string? Message = null);
