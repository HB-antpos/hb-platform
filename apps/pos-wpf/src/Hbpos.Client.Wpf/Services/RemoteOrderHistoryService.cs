using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IRemoteOrderHistoryService
{
    Task<RemoteOrderHistoryResult> QueryAsync(
        RemoteOrderHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<ReceiptDetails?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteOrderHistoryQuery(
    string StoreCode,
    DateTimeOffset? SoldFrom,
    DateTimeOffset? SoldTo,
    string? DeviceCode,
    string? Keyword,
    int Take);

public sealed record RemoteOrderHistoryResult(IReadOnlyList<RemoteOrderHistorySummary> Orders);

public sealed record RemoteOrderHistorySummary(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    string PaymentSummary,
    string StatusLabel);

public sealed class RemoteOrderHistoryService(IOrderHistoryApiClient apiClient) : IRemoteOrderHistoryService
{
    public async Task<RemoteOrderHistoryResult> QueryAsync(
        RemoteOrderHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var response = await apiClient.QueryAsync(new OrderHistoryQueryRequest(
            query.StoreCode,
            query.DeviceCode,
            query.SoldFrom,
            query.SoldTo,
            query.Keyword,
            Math.Clamp(query.Take, 1, 200)), cancellationToken);

        return new RemoteOrderHistoryResult(response.Orders.Select(order => new RemoteOrderHistorySummary(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.LineCount,
            order.PaymentSummary,
            order.StatusLabel)).ToList());
    }

    public async Task<ReceiptDetails?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        var details = await apiClient.GetDetailsAsync(orderGuid, cancellationToken);
        if (details is null)
        {
            return null;
        }

        var payments = details.Payments.Select(payment => new ReceiptPaymentLine(
            payment.Method,
            payment.Amount,
            payment.Reference,
            payment.CardTransactions)).ToList();
        return new ReceiptDetails(
            details.OrderGuid,
            details.StoreCode,
            details.DeviceCode,
            details.CashierName,
            details.SoldAt,
            details.TotalAmount,
            details.DiscountAmount,
            details.ActualAmount,
            details.Lines.Select(line => new ReceiptPreviewLine(
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)
            {
                ProductCode = line.ProductCode,
                ItemNumber = line.ItemNumber
            }).ToList(),
            payments,
            RefundVoucher: ReceiptRefundVoucherMapper.TryCreate(payments));
    }

    public Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        return apiClient.GetReturnContextAsync(orderGuid, cancellationToken);
    }

    public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return apiClient.CreateReturnRecordsAsync(request, cancellationToken);
    }
}

public interface IOrderHistoryApiClient
{
    Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OrderHistoryApiClient(HttpClient httpClient) : IOrderHistoryApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    public async Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/orders/history",
            ("storeCode", request.StoreCode),
            ("deviceCode", request.DeviceCode),
            ("soldFrom", request.SoldFrom?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("soldTo", request.SoldTo?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("keyword", request.Keyword),
            ("take", request.Take.ToString(CultureInfo.InvariantCulture)));
            using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryTimeout.CancelAfter(QueryTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await SendAsync<OrderHistoryQueryResponse>(
                token => httpClient.GetAsync(requestUri, token),
                "GET",
                "api/v1/orders/history",
                traceId: null,
                queryTimeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 自建 2 秒超时（调用方未取消）：单独记 Warning，便于区分慢查询与断网。
            LogFailure(
                "GET",
                "api/v1/orders/history",
                traceId: null,
                statusCode: null,
                errorCode: "ORDER_HISTORY_QUERY_TIMEOUT",
                stopwatch.ElapsedMilliseconds,
                ex,
                reason: "timeout");
            throw new CatalogApiException(
                "在线订单查询超过 2 秒，请缩小日期范围后重试。 / Online order search exceeded 2 seconds. Narrow the date range and retry.",
                HttpStatusCode.RequestTimeout,
                "ORDER_HISTORY_QUERY_TIMEOUT",
                ex);
        }
    }

    public async Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/orders/history/{Uri.EscapeDataString(orderGuid.ToString("D"))}";
        return await SendAsync<OrderHistoryDetailsDto?>(
            token => httpClient.GetAsync(path, token),
            "GET",
            path,
            orderGuid.ToString("D"),
            cancellationToken);
    }

    public async Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/orders/history/{Uri.EscapeDataString(orderGuid.ToString("D"))}/return-context";
        return await SendAsync<OrderReturnContextDto?>(
            token => httpClient.GetAsync(path, token),
            "GET",
            path,
            orderGuid.ToString("D"),
            cancellationToken);
    }

    public async Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<OrderReturnRecordCreateResponse>(
            token => httpClient.PostAsJsonAsync("api/v1/orders/returns", request, JsonOptions, token),
            "POST",
            "api/v1/orders/returns",
            request.ReturnOrderGuid.ToString("D"),
            cancellationToken);
    }

    /// <summary>
    /// 发送并解析；失败（断网、非 2xx、非法 JSON、success=false）统一记一条 Warning 后原样抛出。
    /// 查询类接口成功不记，避免历史页翻查刷屏；超时与取消由调用方处理。
    /// </summary>
    private static async Task<T> SendAsync<T>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        string method,
        string requestPath,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await send(cancellationToken);
            return await ReadApiResultAsync<T>(response, cancellationToken);
        }
        catch (CatalogApiException ex)
        {
            LogFailure(method, requestPath, traceId, ex.StatusCode is { } status ? (int)status : null, ex.ErrorCode, stopwatch.ElapsedMilliseconds, ex, reason: null);
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogFailure(method, requestPath, traceId, statusCode: null, errorCode: null, stopwatch.ElapsedMilliseconds, ex, reason: "network");
            throw;
        }
    }

    private static void LogFailure(
        string method,
        string requestPath,
        string? traceId,
        int? statusCode,
        string? errorCode,
        long elapsedMs,
        Exception exception,
        string? reason)
    {
        ConsoleLog.WriteWarning(
            "OrderHistory",
            $"order history api failed method={method} path={requestPath} " +
            $"http={statusCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} errorCode={errorCode ?? "<null>"} " +
            (reason is null ? string.Empty : $"reason={reason} ") +
            $"message={exception.Message} elapsedMs={elapsedMs}",
            new ApplicationLogContext(
                TraceId: traceId,
                RequestPath: requestPath,
                RequestMethod: method,
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["errorCode"] = errorCode,
                    ["reason"] = reason,
                    ["elapsedMs"] = elapsedMs
                }),
            exception);
    }

    private static async Task<T> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new CatalogApiException(
                    "Order history API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Order history API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            throw new CatalogApiException(
                "Order history API returned an empty response.",
                response.StatusCode);
        }

        if (!result.Success)
        {
            throw new CatalogApiException(
                result.Message ?? "Order history API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        return result.Data!;
    }

    private static string BuildUri(string path, params (string Name, string? Value)[] query)
    {
        var queryString = string.Join(
            "&",
            query
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value!)}"));

        return string.IsNullOrEmpty(queryString)
            ? path
            : $"{path}?{queryString}";
    }
}
