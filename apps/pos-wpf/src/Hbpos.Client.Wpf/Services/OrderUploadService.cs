using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Diagnostics;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface IOrderUploadService
{
    Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default);
}

public sealed class OrderUploadAuthorizationRequiredException(string message, Exception innerException)
    : Exception(message, innerException);

// HttpClient 超时：服务器是否已收到未知，订单仍在队列等同一订单号重试。刻意不是取消异常，
// 调用方据此把它当作"未完成"的上传失败，而不是端点切换或调用方取消。
public sealed class OrderUploadTimeoutException(string message, Exception innerException)
    : TimeoutException(message, innerException);

public interface IOrderUploadExecutionService
{
    Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default);

    async Task<OrderUploadExecutionResult> ExecuteSelectedAsync(
        IReadOnlyCollection<Guid> orderGuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderGuids);
        var selected = orderGuids.Where(id => id != Guid.Empty).Distinct().ToArray();
        var uploaded = 0;
        var failed = 0;
        foreach (var orderGuid in selected)
        {
            var result = await ExecuteOneAsync(orderGuid, cancellationToken);
            uploaded += result.UploadedCount;
            failed += result.FailedCount;
        }

        return new OrderUploadExecutionResult(selected.Length, uploaded, failed);
    }
}

public sealed class OrderUploadService(
    ILocalOrderRepository orderRepository,
    IOrderSyncApiClient apiClient,
    ILocalOrderUploadRepository uploadRepository) : IOrderUploadService
{
    internal const string UploadTimedOutMessage =
        "Order upload timed out. The server may already have received it; the order stays queued and will be retried with the same order ID.";

    public async Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Log($"upload start orderGuid={orderGuid:D}");
        var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken)
            ?? throw new InvalidOperationException("Order was not found for upload.");
        // 服务端 200 但 Accepted=false 时已在抛出前记过 Warning，catch 里不再重复记。
        var notAcceptedLogged = false;
        try
        {
            await uploadRepository.MarkSyncingAsync(orderGuid, cancellationToken);
            Log(
                $"mark syncing orderGuid={orderGuid:D} store={order.StoreCode} device={order.DeviceCode} " +
                $"lines={order.Lines.Count} payments={order.Payments.Count} actualAmount={order.ActualAmount}");
            var request = ToRequest(order);
            var response = await apiClient.SyncAsync(request, cancellationToken);
            if (!response.Accepted)
            {
                // 服务端 200 却明确不收：这是业务拒绝，按订单节流记 Warning，下面的 catch 负责标 Failed。
                var notAcceptedAttempt = OrderUploadFailureLogThrottle.RecordFailure(orderGuid);
                if (OrderUploadFailureLogThrottle.ShouldReport(notAcceptedAttempt))
                {
                    ConsoleLog.WriteWarning(
                        "OrderSync",
                        $"upload not accepted orderGuid={orderGuid:D} attempt={notAcceptedAttempt} " +
                        $"message={response.Message ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}",
                        CreateUploadLogContext(order, notAcceptedAttempt, stopwatch.ElapsedMilliseconds, "not-accepted"));
                }

                notAcceptedLogged = true;
                throw new InvalidOperationException(response.Message ?? "Order sync was not accepted.");
            }

            await uploadRepository.MarkSyncedAsync(orderGuid, cancellationToken);
            OrderUploadFailureLogThrottle.RecordSuccess(orderGuid);
            Log(
                $"upload completed orderGuid={orderGuid:D} accepted={response.Accepted} alreadySynced={response.AlreadySynced} " +
                $"heldOrderDisposition={response.HeldOrderDisposition} " +
                $"message={response.Message ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
            WarnWhenHeldOrderUnmatched(orderGuid, order, response);
        }
        catch (CatalogApiException ex) when (
            ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 收银员票据缺失或过期不是订单失败；恢复 Pending，等待下一次有效登录。
            await uploadRepository.MarkPendingAsync(orderGuid, cancellationToken);
            Log($"upload deferred orderGuid={orderGuid:D} reason=cashier-authorization-required");
            throw new OrderUploadAuthorizationRequiredException("需要有效收银员授权后再上传订单。", ex);
        }
        catch (TaskCanceledException ex) when (
            !cancellationToken.IsCancellationRequested && ex.InnerException is TimeoutException)
        {
            // HttpClient 超时与端点代际取消都是 TaskCanceledException，只有超时带内层 TimeoutException。
            // 超时后服务器是否已收到未知：订单留在队列并写明原因，同一订单号重试由服务端去重；
            // 改抛非取消异常，让批量上传按"未完成"计数并继续下一笔，不再当作端点切换中断整批。
            await uploadRepository.MarkPendingAsync(orderGuid, UploadTimedOutMessage, CancellationToken.None);
            Log($"upload timed out orderGuid={orderGuid:D} elapsedMs={stopwatch.ElapsedMilliseconds}");
            var timeoutAttempt = OrderUploadFailureLogThrottle.RecordFailure(orderGuid);
            if (OrderUploadFailureLogThrottle.ShouldReport(timeoutAttempt))
            {
                ConsoleLog.WriteWarning(
                    "OrderSync",
                    $"upload timed out orderGuid={orderGuid:D} attempt={timeoutAttempt} reason=timeout " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}",
                    CreateUploadLogContext(order, timeoutAttempt, stopwatch.ElapsedMilliseconds, "timeout"),
                    ex);
            }

            throw new OrderUploadTimeoutException(UploadTimedOutMessage, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 旧端点代际被切换取消不属于订单失败；恢复 Pending，交给新端点继续上传。
            await uploadRepository.MarkPendingAsync(orderGuid, CancellationToken.None);
            Log($"upload deferred orderGuid={orderGuid:D} reason=endpoint-generation-canceled");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await uploadRepository.MarkFailedAsync(orderGuid, ex.Message, cancellationToken);
            Log(
                $"upload failed orderGuid={orderGuid:D} error={ex.GetType().Name} message={ex.Message} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            // CatalogApiException 与响应解析失败（JsonException）已由 HTTP 客户端按状态码分级记录；Accepted=false 已在上面记录。
            // 这里只补断网、本地异常等客户端看不到的失败，同样按订单节流。
            if (ex is not (CatalogApiException or JsonException) && !notAcceptedLogged)
            {
                var attempt = OrderUploadFailureLogThrottle.RecordFailure(orderGuid);
                if (OrderUploadFailureLogThrottle.ShouldReport(attempt))
                {
                    ConsoleLog.WriteWarning(
                        "OrderSync",
                        $"upload failed orderGuid={orderGuid:D} attempt={attempt} error={ex.GetType().Name} " +
                        $"message={ex.Message} elapsedMs={stopwatch.ElapsedMilliseconds}",
                        CreateUploadLogContext(
                            order,
                            attempt,
                            stopwatch.ElapsedMilliseconds,
                            ex is HttpRequestException ? "network" : null),
                        ex);
                }
            }

            throw;
        }
    }

    private static OrderSyncRequest ToRequest(LocalOrder order)
    {
        return new OrderSyncRequest(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierId,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.Lines.Select(line => new OrderLineSyncDto(
                line.OrderLineGuid,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount,
                line.PriceSource,
                line.ItemNumber,
                line.Kind,
                line.ReturnSourceKey,
                line.OriginalOrderGuid,
                line.OriginalOrderDetailGuid)).ToList(),
            order.Payments.Select(ToPaymentSyncDto).ToList(),
            order.HeldOrderSource);
    }

    private static PaymentSyncDto ToPaymentSyncDto(LocalPayment payment)
    {
        if (payment.Method == PaymentMethodKind.Voucher)
        {
            var (voucherCode, reservationToken) = ParseVoucherReference(payment.Reference);
            return new PaymentSyncDto(
                payment.PaymentGuid,
                payment.Method,
                payment.Amount,
                voucherCode,
                reservationToken,
                payment.CardTransactions);
        }

        return new PaymentSyncDto(
            payment.PaymentGuid,
            payment.Method,
            payment.Amount,
            payment.Reference,
            CardTransactions: payment.CardTransactions);
    }

    internal static (string VoucherCode, string ReservationToken) ParseVoucherReference(string? reference)
    {
        var parts = (reference ?? string.Empty).Split(':', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)
            ? (parts[1], parts[2])
            : parts.Length >= 2 && parts[0].Equals("VOUCHER_REFUND", StringComparison.OrdinalIgnoreCase)
                ? (parts[1], string.Empty)
            : (reference ?? string.Empty, string.Empty);
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("OrderSync", message);
    }

    private static ApplicationLogContext CreateUploadLogContext(
        LocalOrder order,
        int attempt,
        long elapsedMs,
        string? reason)
    {
        return new ApplicationLogContext(
            TraceId: order.OrderGuid.ToString("D"),
            RequestPath: OrderSyncApiClient.RequestPath,
            RequestMethod: "POST",
            Properties: new Dictionary<string, object?>
            {
                ["storeCode"] = order.StoreCode,
                ["deviceCode"] = order.DeviceCode,
                ["attemptCount"] = attempt,
                ["elapsedMs"] = elapsedMs,
                ["reason"] = reason
            });
    }

    private static void WarnWhenHeldOrderUnmatched(
        Guid orderGuid,
        LocalOrder order,
        OrderSyncResponse response)
    {
        if (response.HeldOrderDisposition != HeldOrderDisposition.Unmatched)
        {
            return;
        }

        // 关键逻辑：Unmatched 表示订单本身已落库，但服务端没能把它关联到对应的共享挂单。
        // 典型成因是本机离线期间该挂单被主管强制释放、并被另一台收银机取走卖出，
        // 此时同一批货会存在两张正式订单。订单不能因此不落库——钱已经收了——
        // 但必须留下错误级别的可告警信号交人工对账，不能静默通过。
        ConsoleLog.WriteError(
            "OrderSync",
            $"upload held-order unmatched orderGuid={orderGuid:D} store={order.StoreCode} device={order.DeviceCode} " +
            $"actualAmount={order.ActualAmount} 挂单关联失败，可能与其他收银机重复销售，需人工对账。");
    }
}

public sealed class OrderUploadExecutionService(
    IOrderUploadService uploadService,
    ILocalOrderUploadRepository uploadRepository) : IOrderUploadExecutionService
{
    // ponytail: 单客户端全局串行足以消除状态覆盖；仅在实测吞吐不足时升级为按订单 GUID 加锁。
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    // 单笔超时只算这笔未完成，继续下一笔，避免一张慢单挡住整个队列；连续两笔超时说明服务端整体不可用，
    // 停止本轮，免得每笔都等满超时（按日期重传一批可达 500 笔）。未尝试的订单留在原状态等下一轮。
    private const int MaxConsecutiveUploadTimeouts = 2;

    public Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default)
    {
        return uploadRepository.GetReuploadableOrderGuidsAsync(soldFrom, soldTo, deviceCode, cancellationToken);
    }

    public async Task<OrderUploadExecutionResult> ExecuteSelectedAsync(
        IReadOnlyCollection<Guid> orderGuids,
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            ArgumentNullException.ThrowIfNull(orderGuids);
            // 保留收银员勾选顺序并去重，串行上传可避免同一订单的状态互相覆盖。
            var selected = orderGuids.Where(id => id != Guid.Empty).Distinct().ToArray();
            var uploadedCount = 0;
            var failedCount = 0;
            var wasInterrupted = false;
            var consecutiveTimeouts = 0;
            for (var index = 0; index < selected.Length; index++)
            {
                var orderGuid = selected[index];
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await uploadService.UploadOrderAsync(orderGuid, cancellationToken);
                    uploadedCount++;
                    consecutiveTimeouts = 0;
                }
                catch (Exception ex) when (IsEndpointTransitionInterruption(ex, cancellationToken))
                {
                    // 端点发布前新请求仍被封闭；将当前项和剩余选择统一入队，交给新端点继续上传。
                    for (var pendingIndex = index; pendingIndex < selected.Length; pendingIndex++)
                    {
                        await uploadRepository.MarkPendingAsync(selected[pendingIndex], cancellationToken);
                    }

                    failedCount += selected.Length - index;
                    wasInterrupted = true;
                    Log(
                        $"execute selected batch interrupted orderGuid={orderGuid:D} queued={selected.Length - index} " +
                        "reason=endpoint-transition " +
                        $"error={ex.GetType().Name}");
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (OrderUploadAuthorizationRequiredException)
                {
                    failedCount++;
                    consecutiveTimeouts = 0;
                    Log($"execute selected item deferred orderGuid={orderGuid:D} reason=cashier-authorization-required");
                }
                catch (OrderUploadTimeoutException)
                {
                    failedCount++;
                    if (++consecutiveTimeouts >= MaxConsecutiveUploadTimeouts)
                    {
                        var skipped = selected.Length - index - 1;
                        failedCount += skipped;
                        Log(
                            $"execute selected stopped orderGuid={orderGuid:D} reason=consecutive-timeouts " +
                            $"timeouts={consecutiveTimeouts} skipped={skipped}");
                        break;
                    }

                    Log($"execute selected item timed out orderGuid={orderGuid:D} continue=true");
                }
                catch (Exception ex)
                {
                    failedCount++;
                    consecutiveTimeouts = 0;
                    Log($"execute selected item failed orderGuid={orderGuid:D} error={ex.GetType().Name} message={ex.Message}");
                }
            }

            return new OrderUploadExecutionResult(selected.Length, uploadedCount, failedCount, wasInterrupted);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Log($"execute one start orderGuid={orderGuid:D}");
            try
            {
                await uploadService.UploadOrderAsync(orderGuid, cancellationToken);
                Log($"execute one completed orderGuid={orderGuid:D} uploaded=1 failed=0 elapsedMs={stopwatch.ElapsedMilliseconds}");
                return new OrderUploadExecutionResult(1, 1, 0);
            }
            catch (Exception ex) when (IsEndpointTransitionInterruption(ex, cancellationToken))
            {
                Log(
                    $"execute one interrupted orderGuid={orderGuid:D} reason=endpoint-transition " +
                    $"error={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
                return new OrderUploadExecutionResult(1, 0, 0, WasInterrupted: true);
            }
            catch (OperationCanceledException)
            {
                Log($"execute one canceled orderGuid={orderGuid:D} elapsedMs={stopwatch.ElapsedMilliseconds}");
                throw;
            }
            catch (OrderUploadAuthorizationRequiredException)
            {
                Log($"execute one deferred orderGuid={orderGuid:D} reason=cashier-authorization-required");
                return new OrderUploadExecutionResult(1, 0, 0);
            }
            catch (Exception ex)
            {
                Log($"execute one failed orderGuid={orderGuid:D} error={ex.GetType().Name} message={ex.Message} elapsedMs={stopwatch.ElapsedMilliseconds}");
                return new OrderUploadExecutionResult(1, 0, 1);
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Log($"execute pending start batchSize={batchSize}");
            var orderGuids = await uploadRepository.GetPendingOrderGuidsAsync(batchSize, cancellationToken);
            Log($"execute pending queued count={orderGuids.Count} batchSize={batchSize}");
            var uploadedCount = 0;
            var failedCount = 0;
            var wasInterrupted = false;
            var consecutiveTimeouts = 0;

            for (var index = 0; index < orderGuids.Count; index++)
            {
                var orderGuid = orderGuids[index];
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await uploadService.UploadOrderAsync(orderGuid, cancellationToken);
                    uploadedCount++;
                    consecutiveTimeouts = 0;
                    Log($"execute pending item completed orderGuid={orderGuid:D} uploadedCount={uploadedCount} failedCount={failedCount}");
                }
                catch (Exception ex) when (IsEndpointTransitionInterruption(ex, cancellationToken))
                {
                    wasInterrupted = true;
                    Log(
                        $"execute pending interrupted orderGuid={orderGuid:D} uploaded={uploadedCount} failed={failedCount} " +
                        $"reason=endpoint-transition error={ex.GetType().Name} elapsedMs={stopwatch.ElapsedMilliseconds}");
                    break;
                }
                catch (OperationCanceledException)
                {
                    Log(
                        $"execute pending canceled orderGuid={orderGuid:D} attempted={orderGuids.Count} uploaded={uploadedCount} " +
                        $"failed={failedCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
                    throw;
                }
                catch (OrderUploadAuthorizationRequiredException)
                {
                    consecutiveTimeouts = 0;
                    Log($"execute pending item deferred orderGuid={orderGuid:D} reason=cashier-authorization-required");
                }
                catch (OrderUploadTimeoutException)
                {
                    failedCount++;
                    if (++consecutiveTimeouts >= MaxConsecutiveUploadTimeouts)
                    {
                        var skipped = orderGuids.Count - index - 1;
                        failedCount += skipped;
                        Log(
                            $"execute pending stopped orderGuid={orderGuid:D} reason=consecutive-timeouts " +
                            $"timeouts={consecutiveTimeouts} skipped={skipped} elapsedMs={stopwatch.ElapsedMilliseconds}");
                        break;
                    }

                    Log($"execute pending item timed out orderGuid={orderGuid:D} continue=true");
                }
                catch (Exception ex)
                {
                    failedCount++;
                    consecutiveTimeouts = 0;
                    Log(
                        $"execute pending item failed orderGuid={orderGuid:D} uploadedCount={uploadedCount} failedCount={failedCount} " +
                        $"error={ex.GetType().Name} message={ex.Message}");
                }
            }

            Log(
                $"execute pending completed attempted={orderGuids.Count} uploaded={uploadedCount} failed={failedCount} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new OrderUploadExecutionResult(orderGuids.Count, uploadedCount, failedCount, wasInterrupted);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("OrderSync", message);
    }

    private static bool IsEndpointTransitionInterruption(Exception exception, CancellationToken callerToken)
    {
        return exception is OperationCanceledException && !callerToken.IsCancellationRequested;
    }
}

public sealed class NoopOrderUploadExecutionService : IOrderUploadExecutionService
{
    public static NoopOrderUploadExecutionService Instance { get; } = new();

    private NoopOrderUploadExecutionService()
    {
    }

    public Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    public Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderUploadExecutionResult(0, 0, 0));
    }

    public Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderUploadExecutionResult(0, 0, 0));
    }

    public Task<OrderUploadExecutionResult> ExecuteSelectedAsync(
        IReadOnlyCollection<Guid> orderGuids,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderUploadExecutionResult(orderGuids?.Distinct().Count() ?? 0, 0, 0));
    }
}

public interface IOrderSyncApiClient
{
    Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default);
}

public sealed class OrderSyncApiClient(HttpClient httpClient) : IOrderSyncApiClient
{
    internal const string RequestPath = "/api/v1/orders/sync";
    private const int MaxLoggedBodyLength = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        const string requestPath = RequestPath;
        Log(
            $"http sync start orderGuid={request.OrderGuid:D} store={request.StoreCode} device={request.DeviceCode} " +
            $"lines={request.Lines.Count} payments={request.Payments.Count}");
        using var response = await httpClient.PostAsJsonAsync(requestPath.TrimStart('/'), request, JsonOptions, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<OrderSyncResponse>? result = null;
        JsonException? parseException = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<OrderSyncResponse>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                // 网关 502 HTML 等非 JSON 响应：先记下状态码与失败日志，再在下面按原样抛出 JsonException。
                parseException = ex;
            }
        }

        if (parseException is not null ||
            !response.IsSuccessStatusCode || result is null || !result.Success || result.Data is null)
        {
            var statusCode = (int)response.StatusCode;
            // 服务端错误正文只在非 2xx 时截断 256 字符写入日志，2xx 正文可能含订单数据。
            var bodySnippet = response.IsSuccessStatusCode ? null : TruncateForLog(content);
            var attempt = OrderUploadFailureLogThrottle.RecordFailure(request.OrderGuid);
            Log(
                $"http sync failed orderGuid={request.OrderGuid:D} http={statusCode} " +
                $"success={result?.Success.ToString() ?? "<null>"} errorCode={result?.ErrorCode ?? "<null>"} " +
                $"message={result?.Message ?? "<null>"} attempt={attempt} " +
                $"invalidJson={parseException is not null} elapsedMs={stopwatch.ElapsedMilliseconds}");
            // 同一订单每 15 秒重试一次：只在第 1、2、4、8… 次失败写中心告警，其余只留上面的 Information。
            if (OrderUploadFailureLogThrottle.ShouldReport(attempt))
            {
                var message =
                    $"Order sync request failed. orderGuid={request.OrderGuid:D} http={statusCode} " +
                    $"errorCode={result?.ErrorCode ?? "<null>"} message={result?.Message ?? "<null>"} attempt={attempt}" +
                    (parseException is null ? string.Empty : " reason=invalid-json") +
                    (bodySnippet is null ? string.Empty : $" body={bodySnippet}");
                var context = new ApplicationLogContext(
                    TraceId: request.OrderGuid.ToString("D"),
                    RequestPath: requestPath,
                    RequestMethod: "POST",
                    StatusCode: statusCode,
                    Properties: new Dictionary<string, object?>
                    {
                        ["storeCode"] = request.StoreCode,
                        ["deviceCode"] = request.DeviceCode,
                        ["errorCode"] = result?.ErrorCode,
                        ["attemptCount"] = attempt,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                    });
                // 5xx 与 2xx 解析失败是本应成功的确定性失败（Error）；401/403 是授权状态、其余 4xx 与业务拒绝为 Warning。
                if (statusCode >= 500 || (parseException is not null && response.IsSuccessStatusCode))
                {
                    ConsoleLog.WriteError("OrderSync", message, context, parseException);
                }
                else
                {
                    ConsoleLog.WriteWarning("OrderSync", message, context, parseException);
                }
            }

            if (parseException is not null)
            {
                // 日志已带状态码；异常形状保持原来的 JsonException，上传状态流转（Failed / 待授权）不随日志改动变化。
                ExceptionDispatchInfo.Throw(parseException);
            }

            throw new CatalogApiException(
                result?.Message ?? $"Order sync failed with HTTP {statusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        OrderUploadFailureLogThrottle.RecordSuccess(request.OrderGuid);
        Log(
            $"http sync completed orderGuid={request.OrderGuid:D} http={(int)response.StatusCode} accepted={result.Data.Accepted} " +
            $"alreadySynced={result.Data.AlreadySynced} message={result.Data.Message ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return result.Data;
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("OrderSync", message);
    }

    private static string? TruncateForLog(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content.Trim();
        return text.Length <= MaxLoggedBodyLength ? text : text[..MaxLoggedBodyLength];
    }
}

/// <summary>
/// 订单上传失败日志节流（只影响日志，不影响重试节奏）：Failed 订单会被 15 秒一轮的上传队列反复重试，
/// 同一 orderGuid 在本进程内只在第 1、2、4、8、16… 次连续失败时写 Warning/Error，成功后清零。
/// </summary>
internal static class OrderUploadFailureLogThrottle
{
    // 防止长期失败的订单无限增长：超过上限直接清空，最坏情况只是多记几条日志。
    private const int MaxTrackedOrders = 2000;
    private static readonly ConcurrentDictionary<Guid, int> ConsecutiveFailures = new();

    internal static int RecordFailure(Guid orderGuid)
    {
        if (ConsecutiveFailures.Count >= MaxTrackedOrders && !ConsecutiveFailures.ContainsKey(orderGuid))
        {
            ConsecutiveFailures.Clear();
        }

        return ConsecutiveFailures.AddOrUpdate(
            orderGuid,
            1,
            (_, count) => count == int.MaxValue ? count : count + 1);
    }

    internal static void RecordSuccess(Guid orderGuid)
    {
        ConsecutiveFailures.TryRemove(orderGuid, out _);
    }

    internal static bool ShouldReport(int attempt) => attempt > 0 && (attempt & (attempt - 1)) == 0;
}

public interface ILocalOrderUploadRepository
{
    Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default);

    Task MarkSyncingAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task MarkPendingAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    // 留在队列等重试，同时记下原因（如上传超时），供同步中心显示。
    Task MarkPendingAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default);

    Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default);
}

public sealed class LocalOrderUploadRepository(LocalSqliteStore store) : ILocalOrderUploadRepository
{
    public async Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT EntityId
            FROM SyncQueue
            WHERE EntityType = 'Order'
              AND Status IN ('Pending', 'Failed')
            ORDER BY CreatedAt
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 100));

        var orderGuids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var orderGuid))
            {
                orderGuids.Add(orderGuid);
            }
        }

        return orderGuids;
    }

    public async Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default)
    {
        if (soldFrom > soldTo)
        {
            return [];
        }

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrderGuid
            FROM LocalOrders
            WHERE julianday(SoldAt) >= julianday($SoldFrom)
              AND julianday(SoldAt) <= julianday($SoldTo)
              AND ($DeviceCode IS NULL OR DeviceCode = $DeviceCode COLLATE NOCASE)
              AND SyncStatus IN ('Synced', 'Pending', 'Failed')
            ORDER BY julianday(SoldAt) ASC, OrderGuid ASC;
            """;
        command.Parameters.AddWithValue("$SoldFrom", soldFrom.ToString("O"));
        command.Parameters.AddWithValue("$SoldTo", soldTo.ToString("O"));
        command.Parameters.AddWithValue(
            "$DeviceCode",
            string.IsNullOrWhiteSpace(deviceCode) ? DBNull.Value : deviceCode.Trim());

        var orderGuids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var orderGuid))
            {
                orderGuids.Add(orderGuid);
            }
        }

        return orderGuids;
    }

    public Task MarkSyncingAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Syncing", null, cancellationToken);
    }

    public Task MarkPendingAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Pending", null, cancellationToken);
    }

    public Task MarkPendingAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Pending", errorMessage, cancellationToken);
    }

    public Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Synced", null, cancellationToken);
    }

    public Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Failed", errorMessage, cancellationToken);
    }

    private async Task UpdateStatusAsync(
        Guid orderGuid,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var orderCommand = connection.CreateCommand())
        {
            orderCommand.Transaction = transaction;
            orderCommand.CommandText = "UPDATE LocalOrders SET SyncStatus = $Status WHERE OrderGuid = $OrderGuid;";
            orderCommand.Parameters.AddWithValue("$Status", status);
            orderCommand.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
            await orderCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var queueCommand = connection.CreateCommand())
        {
            queueCommand.Transaction = transaction;
            queueCommand.CommandText = """
                UPDATE SyncQueue
                SET Status = $Status,
                    LastTriedAt = $LastTriedAt,
                    ErrorMessage = $ErrorMessage
                WHERE EntityId = $OrderGuid AND EntityType = 'Order';
                """;
            queueCommand.Parameters.AddWithValue("$Status", status == "Synced" ? "Synced" : status);
            queueCommand.Parameters.AddWithValue("$LastTriedAt", DateTimeOffset.Now.ToString("O"));
            queueCommand.Parameters.AddWithValue("$ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            queueCommand.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
            await queueCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed record OrderUploadExecutionResult(
    int AttemptedCount,
    int UploadedCount,
    int FailedCount,
    bool WasInterrupted = false);
