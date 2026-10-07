using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Vouchers;

namespace Hbpos.Client.Wpf.Services;

public interface IVoucherApiClient : IVoucherTenderClient
{
    Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken = default);

    Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken = default);

    Task<StoreVoucherReleaseResponse> ReleaseAsync(
        StoreVoucherReleaseRequest request,
        CancellationToken cancellationToken = default);

    Task<StoreVoucherIssueRefundResponse> IssueRefundVoucherAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken = default);

    Task<StoreVoucherIssueResponse> IssueVoucherAsync(
        StoreVoucherIssueRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class VoucherApiClient(HttpClient httpClient, ILocalizationService? localization = null) : IVoucherApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/vouchers/{Uri.EscapeDataString(voucherCode)}?storeCode={Uri.EscapeDataString(storeCode)}";
        // 券号在路径里：日志只用模板路径，券号只留后 4 位。
        return GetAsync<StoreVoucherQueryResponse>(
            path,
            new VoucherRequestLog("query", "api/v1/vouchers/{voucherCode}", storeCode, voucherCode),
            cancellationToken);
    }

    public async Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken = default)
    {
        var log = new VoucherRequestLog("lock", "api/v1/vouchers/lock", request.StoreCode, request.VoucherCode);
        var stopwatch = Stopwatch.StartNew();
        var response = await PostAsync<StoreVoucherLockRequest, StoreVoucherLockResponse>(
            "api/v1/vouchers/lock",
            request,
            log,
            cancellationToken);
        LogSucceeded(
            log,
            $"requestedAmount={request.RequestedAmount:0.00} lockedAmount={response.LockedAmount:0.00} " +
            $"remainingAfterLock={response.RemainingAmountAfterLock?.ToString("0.00") ?? "<null>"}",
            stopwatch);
        return response;
    }

    public Task<StoreVoucherReleaseResponse> ReleaseAsync(
        StoreVoucherReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<StoreVoucherReleaseRequest, StoreVoucherReleaseResponse>(
            "api/v1/vouchers/release",
            request,
            new VoucherRequestLog("release", "api/v1/vouchers/release", request.StoreCode, request.VoucherCode),
            cancellationToken);
    }

    public async Task<StoreVoucherIssueRefundResponse> IssueRefundVoucherAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken = default)
    {
        var log = new VoucherRequestLog("refund-issue", "api/v1/vouchers/refund", request.StoreCode, null, request.OrderReference);
        var stopwatch = Stopwatch.StartNew();
        var response = await PostAsync<StoreVoucherIssueRefundRequest, StoreVoucherIssueRefundResponse>(
            "api/v1/vouchers/refund",
            request,
            log,
            cancellationToken);
        LogSucceeded(
            log,
            $"amount={response.Amount:0.00} newVoucherTail={VoucherCodeLogFormat.Tail(response.VoucherCode)}",
            stopwatch);
        return response;
    }

    public async Task<StoreVoucherIssueResponse> IssueVoucherAsync(
        StoreVoucherIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        var log = new VoucherRequestLog("issue", "api/v1/vouchers/issue", request.StoreCode, null);
        var stopwatch = Stopwatch.StartNew();
        var response = await PostAsync<StoreVoucherIssueRequest, StoreVoucherIssueResponse>(
            "api/v1/vouchers/issue",
            request,
            log,
            cancellationToken);
        LogSucceeded(
            log,
            $"amount={response.Amount:0.00} newVoucherTail={VoucherCodeLogFormat.Tail(response.VoucherCode)}",
            stopwatch);
        return response;
    }

    public async Task<PaymentAuthorizationResult> RedeemAsync(
        decimal amount,
        PosSessionState session,
        string? voucherCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voucherCode))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.codeRequired", "Voucher code is required."));
        }

        var query = await QueryAsync(session.StoreCode, voucherCode.Trim(), cancellationToken);
        if (!query.Found || query.Voucher is null)
        {
            return new PaymentAuthorizationResult(false, null, query.Message ?? T("payment.voucher.status.unavailable", "Voucher is unavailable."));
        }

        var lockAmount = Math.Min(amount, query.Voucher.RemainingAmount);
        var locked = await LockAsync(
            new StoreVoucherLockRequest(session.StoreCode, query.Voucher.VoucherCode, lockAmount),
            cancellationToken);
        var remainingAfterLock = locked.RemainingAmountAfterLock.GetValueOrDefault();
        var reference = remainingAfterLock > 0m
            ? $"VOUCHER:{locked.VoucherCode}:{locked.ReservationToken}:{remainingAfterLock.ToString("0.00", CultureInfo.InvariantCulture)}"
            : $"VOUCHER:{locked.VoucherCode}:{locked.ReservationToken}";

        // 中文注释：前三段仍供上传完成核销；第 4 段只使用后端锁券后的确认余额，避免本地旧快照错印。
        return new PaymentAuthorizationResult(
            true,
            reference,
            locked.VoucherCode,
            locked.LockedAmount);
    }

    public async Task<bool> ReleaseAsync(
        PosSessionState session,
        string voucherCode,
        string reservationToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voucherCode) || string.IsNullOrWhiteSpace(reservationToken))
        {
            return false;
        }

        var released = await ReleaseAsync(
            new StoreVoucherReleaseRequest(session.StoreCode, voucherCode.Trim(), reservationToken.Trim()),
            cancellationToken);
        return released.Released;
    }

    public async Task<PaymentAuthorizationResult> IssueRefundAsync(
        decimal amount,
        PosSessionState session,
        string orderReference,
        string idempotencyKey,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.refundAmountMustBePositive", "Voucher refund amount must be greater than zero."));
        }

        if (string.IsNullOrWhiteSpace(orderReference))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.refundOrderReferenceRequired", "Voucher refund order reference is required."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new PaymentAuthorizationResult(false, null, T("payment.voucher.refundIdempotencyKeyRequired", "Voucher refund idempotency key is required."));
        }

        var issued = await IssueRefundVoucherAsync(
            new StoreVoucherIssueRefundRequest(
                session.StoreCode,
                amount,
                session.CashierId,
                IdempotencyKey: idempotencyKey.Trim(),
                OrderReference: orderReference.Trim(),
                Reason: reason),
            cancellationToken);
        // 退款券是新发券，不需要 reservation token，订单支付引用只保存券码。
        return new PaymentAuthorizationResult(
            true,
            $"VOUCHER_REFUND:{issued.VoucherCode}",
            issued.VoucherCode,
            issued.Amount);
    }

    private Task<TResponse> GetAsync<TResponse>(
        string path,
        VoucherRequestLog log,
        CancellationToken cancellationToken)
    {
        return SendAsync<TResponse>(() => httpClient.GetAsync(path, cancellationToken), "GET", log, cancellationToken);
    }

    private Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        VoucherRequestLog log,
        CancellationToken cancellationToken)
    {
        return SendAsync<TResponse>(
            () => httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken),
            "POST",
            log,
            cancellationToken);
    }

    private static async Task<TResponse> SendAsync<TResponse>(
        Func<Task<HttpResponseMessage>> send,
        string method,
        VoucherRequestLog log,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException ex)
        {
            LogFailed(log, method, null, null, null, stopwatch, ex, "network");
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 自身超时（调用方未取消）：收银员会看到失败并重试。
            LogFailed(log, method, null, null, null, stopwatch, ex, "timeout");
            throw;
        }

        using (response)
        {
            return await ReadAsync<TResponse>(response, method, log, stopwatch, cancellationToken);
        }
    }

    private static async Task<TResponse> ReadAsync<TResponse>(
        HttpResponseMessage response,
        string method,
        VoucherRequestLog log,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<TResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<TResponse>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                // 网关 HTML 等非 JSON：先把状态码记进日志，再按原样抛出 JsonException，调用方的异常分支保持不变。
                LogFailed(log, method, (int)response.StatusCode, null, null, stopwatch, ex, "invalid-json");
                throw;
            }
        }

        if (!response.IsSuccessStatusCode || result is null || !result.Success || result.Data is null)
        {
            LogFailed(log, method, (int)response.StatusCode, result?.ErrorCode, result?.Message, stopwatch, null, null);
            throw new CatalogApiException(
                result?.Message ?? $"Voucher API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        return result.Data;
    }

    private static void LogFailed(
        VoucherRequestLog log,
        string method,
        int? statusCode,
        string? errorCode,
        string? serverMessage,
        Stopwatch stopwatch,
        Exception? exception,
        string? reason)
    {
        // 券相关失败会直接显示给收银员并由其重试，不涉及资金丢失：统一 Warning。
        // 服务端文案可能回显券号，这里不写 message 原文，只记 errorCode。
        ConsoleLog.WriteWarning(
            "Voucher",
            $"voucher api failed op={log.Operation} method={method} path={log.LogPath} " +
            $"http={statusCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} errorCode={errorCode ?? "<null>"} " +
            $"voucherTail={VoucherCodeLogFormat.Tail(log.VoucherCode)} " +
            (log.OrderReference is null ? string.Empty : $"orderReference={log.OrderReference} ") +
            (reason is null ? string.Empty : $"reason={reason} ") +
            $"hasServerMessage={!string.IsNullOrWhiteSpace(serverMessage)} elapsedMs={stopwatch.ElapsedMilliseconds}",
            CreateContext(log, method, statusCode, errorCode, stopwatch, reason),
            exception);
    }

    private static void LogSucceeded(VoucherRequestLog log, string detail, Stopwatch stopwatch)
    {
        // 锁券/发券/退款券都是低频人工操作，成功也记 Information，便于与服务端券流水对账。
        ConsoleLog.WriteInformation(
            "Voucher",
            $"voucher api succeeded op={log.Operation} voucherTail={VoucherCodeLogFormat.Tail(log.VoucherCode)} " +
            (log.OrderReference is null ? string.Empty : $"orderReference={log.OrderReference} ") +
            $"{detail} elapsedMs={stopwatch.ElapsedMilliseconds}",
            CreateContext(log, "POST", null, null, stopwatch, null));
    }

    private static ApplicationLogContext CreateContext(
        VoucherRequestLog log,
        string method,
        int? statusCode,
        string? errorCode,
        Stopwatch stopwatch,
        string? reason)
    {
        return new ApplicationLogContext(
            // 退款券以订单号串联；锁券/查券没有业务 Guid。
            TraceId: log.OrderReference,
            RequestPath: log.LogPath,
            RequestMethod: method,
            StatusCode: statusCode,
            Properties: new Dictionary<string, object?>
            {
                ["storeCode"] = log.StoreCode,
                ["operation"] = log.Operation,
                ["errorCode"] = errorCode,
                ["reason"] = reason,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds
            });
    }

    private sealed record VoucherRequestLog(
        string Operation,
        string LogPath,
        string? StoreCode,
        string? VoucherCode,
        string? OrderReference = null);

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}

/// <summary>
/// 券号可直接兑付，日志里只允许出现后 4 位，并使用 <c>voucherTail=</c> 前缀
/// （<c>voucher=</c> 前缀会被中心脱敏器整段遮盖）。
/// </summary>
internal static class VoucherCodeLogFormat
{
    internal static string Tail(string? voucherCode)
    {
        var trimmed = voucherCode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "<none>";
        }

        // 不足 5 位的券号整段都算敏感，不输出任何字符。
        return trimmed.Length > 4 ? trimmed[^4..] : "<short>";
    }
}
