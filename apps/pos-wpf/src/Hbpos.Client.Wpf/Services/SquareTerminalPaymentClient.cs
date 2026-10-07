using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

public sealed record SquareCheckoutStatusResult(
    string CheckoutId,
    string Status,
    long? AmountCents,
    string? Currency,
    IReadOnlyList<string> PaymentIds,
    string? CancelReason);

public sealed record SquarePaymentStatusResult(
    string PaymentId,
    string Status,
    long AmountCents,
    string Currency,
    string? CardBrand = null,
    string? MaskedCardNumber = null,
    string? AuthCode = null);

public sealed record SquareRefundStatusResult(
    string RefundId,
    string Status,
    string PaymentId,
    long AmountCents,
    string Currency,
    DateTimeOffset? UpdatedAt = null);

public interface ISquareTerminalPaymentClient
{
    Task<SquareCheckoutStatusResult> GetCheckoutAsync(
        CardTerminalSettings settings,
        string checkoutId,
        CancellationToken cancellationToken = default);

    Task<SquarePaymentStatusResult> GetPaymentAsync(
        CardTerminalSettings settings,
        string paymentId,
        CancellationToken cancellationToken = default);

    Task<SquareRefundStatusResult> GetRefundAsync(
        CardTerminalSettings settings,
        string refundId,
        CancellationToken cancellationToken = default);
}

public sealed class SquareTerminalPaymentClient(HttpClient httpClient) : ISquareTerminalPaymentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareCheckoutStatusResult> GetCheckoutAsync(
        CardTerminalSettings settings,
        string checkoutId,
        CancellationToken cancellationToken = default)
    {
        var checkout = await SendApiAsync<SquareCheckoutStatusResponse?>(
            HttpMethod.Get,
            $"api/v1/square/checkouts/{Uri.EscapeDataString(checkoutId)}?environment={Uri.EscapeDataString(settings.Environment.ToString())}",
            operationName: "checkout",
            cancellationToken);

        if (checkout is null)
        {
            throw new JsonException("Square checkout API returned no checkout.");
        }

        // 后端会直接返回 checkout.payment_ids；内嵌 payment id 仅作为兼容补充。
        var paymentIds = new List<string>();
        foreach (var paymentId in checkout.PaymentIds ?? [])
        {
            AddPaymentId(paymentIds, paymentId);
        }

        AddPaymentId(paymentIds, checkout.Payment?.PaymentId);

        return new SquareCheckoutStatusResult(
            checkout.CheckoutId,
            checkout.Status ?? string.Empty,
            checkout.AmountMoney?.Amount,
            checkout.AmountMoney?.Currency,
            paymentIds,
            checkout.CancelReason);
    }

    public async Task<SquarePaymentStatusResult> GetPaymentAsync(
        CardTerminalSettings settings,
        string paymentId,
        CancellationToken cancellationToken = default)
    {
        var payment = await SendApiAsync<SquarePaymentStatusDto?>(
            HttpMethod.Get,
            $"api/v1/square/payments/{Uri.EscapeDataString(paymentId)}?environment={Uri.EscapeDataString(settings.Environment.ToString())}",
            operationName: "payment",
            cancellationToken);

        if (payment is null)
        {
            throw new JsonException("Square payment API returned no payment.");
        }

        // 后端优先返回 approved_money；若上游仅有 total_money，则退回 total_money 保持恢复验证可用。
        var amount = payment.ApprovedMoney ?? payment.TotalMoney;
        if (amount is null || string.IsNullOrWhiteSpace(amount.Currency))
        {
            throw new JsonException("Square payment API returned no approved amount.");
        }

        return new SquarePaymentStatusResult(
            payment.PaymentId,
            payment.Status ?? string.Empty,
            amount.Amount,
            amount.Currency,
            payment.CardBrand,
            payment.MaskedCardNumber,
            payment.AuthCode);
    }

    public async Task<SquareRefundStatusResult> GetRefundAsync(
        CardTerminalSettings settings,
        string refundId,
        CancellationToken cancellationToken = default)
    {
        var refund = await SendApiAsync<SquareRefundResponse?>(
            HttpMethod.Get,
            $"api/v1/square/refunds/{Uri.EscapeDataString(refundId)}?environment={Uri.EscapeDataString(settings.Environment.ToString())}",
            operationName: "refund",
            cancellationToken);

        if (refund is null)
        {
            throw new JsonException("Square refund API returned no refund.");
        }

        if (string.IsNullOrWhiteSpace(refund.PaymentId) ||
            refund.AmountMoney is null ||
            string.IsNullOrWhiteSpace(refund.AmountMoney.Currency))
        {
            throw new JsonException("Square refund API returned incomplete refund evidence.");
        }

        return new SquareRefundStatusResult(
            refund.RefundId,
            refund.Status ?? string.Empty,
            refund.PaymentId,
            refund.AmountMoney.Amount,
            refund.AmountMoney.Currency,
            refund.UpdatedAt);
    }

    private async Task<T> SendApiAsync<T>(
        HttpMethod method,
        string relativeUrl,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        var requestPath = StripQuery(relativeUrl);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage sentResponse;
        try
        {
            sentResponse = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // 断网或 HttpClient 自身超时；调用方取消不记，由上层处理。
            LogFailure(method, requestPath, operationName, statusCode: null, stopwatch.ElapsedMilliseconds,
                ex is HttpRequestException ? "network-error" : "timeout", detail: null, ex);
            throw;
        }

        using var response = sentResponse;
        var content = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        ApiResult<T>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                LogFailure(method, requestPath, operationName, (int)response.StatusCode, stopwatch.ElapsedMilliseconds,
                    "invalid-json", response.IsSuccessStatusCode ? null : Truncate(content), ex);
                throw new JsonException($"Square {operationName} API returned invalid JSON.", ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            LogFailure(method, requestPath, operationName, (int)response.StatusCode, stopwatch.ElapsedMilliseconds,
                "http-error", result?.Message ?? Truncate(content), exception: null);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Message)
                    ? $"Square {operationName} request failed with HTTP {(int)response.StatusCode}."
                    : $"Square {operationName} request failed with HTTP {(int)response.StatusCode}: {result.Message}");
        }

        if (result is null)
        {
            throw new JsonException($"Square {operationName} API returned an empty response.");
        }

        if (!result.Success)
        {
            LogFailure(method, requestPath, operationName, (int)response.StatusCode, stopwatch.ElapsedMilliseconds,
                "failure-response", result.Message, exception: null);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? $"Square {operationName} API returned a failure response."
                    : result.Message);
        }

        return result.Data!;
    }

    /// <summary>
    /// Square 查询（checkout/payment/refund）失败统一记 Warning：方法、路径、状态码、耗时与服务端说明（截断 256 字符）。
    /// 只记失败；查询成功属于恢复流程的高频读取，不记。
    /// </summary>
    private static void LogFailure(
        HttpMethod method,
        string requestPath,
        string operationName,
        int? statusCode,
        long elapsedMs,
        string reason,
        string? detail,
        Exception? exception)
    {
        ConsoleLog.WriteWarning(
            "Square",
            $"Square {operationName} query failed {method.Method} {requestPath} http={statusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<none>"} " +
            $"reason={reason} elapsedMs={elapsedMs}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail={Truncate(detail)}")}",
            new ApplicationLogContext(
                RequestPath: requestPath,
                RequestMethod: method.Method,
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["operation"] = operationName,
                    ["reason"] = reason,
                    ["elapsedMs"] = elapsedMs
                }),
            exception);
    }

    private static string StripQuery(string relativeUrl)
    {
        var index = relativeUrl.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? relativeUrl : relativeUrl[..index];
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 256 ? trimmed : trimmed[..256];
    }

    private static void AddPaymentId(List<string> paymentIds, string? paymentId)
    {
        if (string.IsNullOrWhiteSpace(paymentId) ||
            paymentIds.Any(existing => string.Equals(existing, paymentId, StringComparison.Ordinal)))
        {
            return;
        }

        paymentIds.Add(paymentId.Trim());
    }
}
