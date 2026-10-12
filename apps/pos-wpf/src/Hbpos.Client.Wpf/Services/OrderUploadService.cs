using System.Collections.Concurrent;
using System.Globalization;
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

/// <summary>
/// 全局授权条件不满足：设备授权失效（401），或收银员票据缺失/过期/无上传权限（403 且不是设备范围拒绝）。
/// 与订单本身无关，换哪一笔都会同样被拒；批量上传据此结束本轮，订单恢复 Pending，不计失败次数、不改退避时间。
/// </summary>
public sealed class OrderUploadAuthorizationRequiredException(string message, Exception innerException)
    : Exception(message, innerException);

// HttpClient 超时：服务器是否已收到未知，订单仍在队列等同一订单号重试。刻意不是取消异常，
// 调用方据此把它当作"未完成"的上传失败，而不是端点切换或调用方取消。
public sealed class OrderUploadTimeoutException(string message, Exception innerException)
    : TimeoutException(message, innerException);

/// <summary>
/// 订单里还有未签发的退款券（付款引用仍是占位串 VOUCHER_REFUND_PENDING），拒绝上传且不改任何上传状态。
/// 服务端按订单号去重：带占位引用上传一次后，签券回写的真实券号就再也上不去了。
/// </summary>
public sealed class OrderUploadVoucherRefundPendingException(Guid orderGuid)
    : InvalidOperationException($"Order {orderGuid:D} has a refund voucher that has not been issued yet; upload is held.")
{
    public Guid OrderGuid { get; } = orderGuid;
}

/// <summary>上传失败的退避档位，由 <see cref="OrderUploadRetryPolicy"/> 从异常分类得出。</summary>
public enum OrderUploadFailureKind
{
    /// <summary>网络、5xx、无法分类等暂时性失败：订单置 Failed，退避 30 秒 → 1 → 2 → 5 分钟封顶。</summary>
    Transient,

    /// <summary>HttpClient 超时：服务器是否已收到未知，订单保持 Pending 并写明原因；退避同暂时性档。</summary>
    Timeout,

    /// <summary>服务端明确业务拒绝（可可靠识别的 4xx、Accepted=false）：订单置 Failed，封顶放宽到 30 分钟。</summary>
    BusinessRejection
}

/// <summary>
/// 订单上传失败后的退避策略。只决定后台自动上传什么时候再取到这笔订单；
/// 同步中心的"全部重试/选中重试"、交易历史重传都忽略退避，失败后照样按本策略重新计时。
/// 与 Linkly 结算、日结上传的退避同一思路（见 LinklySettlementUploadService / DailyCloseUploadService）。
/// </summary>
internal static class OrderUploadRetryPolicy
{
    internal const string VoucherRefundPendingReference = "VOUCHER_REFUND_PENDING";

    // 服务端只在设备范围不符时返回这个错误码：订单的门店/设备与当前设备授权不一致（如设备换绑前的旧单），属于单单问题。
    internal const string DeviceScopeForbiddenErrorCode = "DEVICE_SCOPE_FORBIDDEN";

    private static readonly TimeSpan[] TransientDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    // 业务拒绝重试大概率还是被拒：前几档与暂时性相同，5 分钟之后继续翻倍，封顶 30 分钟。
    private static readonly TimeSpan[] BusinessRejectionDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(30)
    ];

    /// <summary>第 attemptCount 次失败（从 1 开始）之后距离下次自动上传的延时；超出档位表按最后一档封顶。</summary>
    internal static TimeSpan GetRetryDelay(int attemptCount, OrderUploadFailureKind failureKind)
    {
        var delays = failureKind == OrderUploadFailureKind.BusinessRejection
            ? BusinessRejectionDelays
            : TransientDelays;
        return delays[Math.Clamp(attemptCount - 1, 0, delays.Length - 1)];
    }

    /// <summary>
    /// 只有能从异常可靠识别出"服务端业务拒绝"才放宽封顶：4xx 且不是 401/403（授权）、404（可能是网关/地址配错）、
    /// 408/429（超时/限流）。其余（网络、5xx、响应解析失败、本地异常）一律按暂时性处理。
    /// </summary>
    internal static OrderUploadFailureKind Classify(Exception exception)
    {
        if (exception is CatalogApiException { StatusCode: { } statusCode })
        {
            var code = (int)statusCode;
            if (code is >= 400 and < 500 &&
                statusCode is not (
                    HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden or
                    HttpStatusCode.NotFound or
                    HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests))
            {
                return OrderUploadFailureKind.BusinessRejection;
            }
        }

        return OrderUploadFailureKind.Transient;
    }

    /// <summary>
    /// 401 是设备授权失效；403 除"设备范围不符"（单单问题）外都来自收银员票据/权限策略，与订单内容无关。
    /// 两者都属全局条件：换哪笔订单都会被拒。
    /// </summary>
    internal static bool IsGlobalAuthorizationFailure(CatalogApiException exception) =>
        exception.StatusCode == HttpStatusCode.Unauthorized ||
        (exception.StatusCode == HttpStatusCode.Forbidden &&
         !string.Equals(exception.ErrorCode, DeviceScopeForbiddenErrorCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>与 CashPaymentWorkflowService 判断"退款券未签发"同口径：trim 后大小写不敏感。</summary>
    internal static bool IsVoucherRefundPendingReference(string? reference) =>
        reference is not null &&
        string.Equals(reference.Trim(), VoucherRefundPendingReference, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 退款券待签发订单的最长暂扣时间（按入队时间算）。暂扣只为挡住"落库 → 签券回写"之间被后台上传插队：
    /// 正常签券几秒完成，收银员在付款页重试签券也在几分钟内。超过这个时间仍是占位引用，说明不会再有人签发
    /// （签券失败后离开付款页，或卡退款经恢复服务落单、从不签券，见 <see cref="OrderUploadVoucherRefundHoldLog"/>），
    /// 此时照旧带占位引用上传并写中心日志 Warning 交人工补券——宁可总部收到一笔待补券号的退货，
    /// 也不能让这笔退货（含已真实发生的卡退款）永远留在本机、总部完全看不到。
    /// </summary>
    internal static readonly TimeSpan VoucherRefundHoldMaxAge = TimeSpan.FromMinutes(30);

    /// <summary>入队时间距今仍在暂扣窗口内才继续暂扣；入队时间未知按"仍在窗口内"处理，宁可多扣一轮。</summary>
    internal static bool IsWithinVoucherRefundHold(DateTimeOffset? queuedAt, DateTimeOffset now) =>
        queuedAt is null || now - queuedAt.Value < VoucherRefundHoldMaxAge;
}

/// <summary>
/// "退款券待签发、上传被暂扣"的中心日志告警。占位引用不会再被签发的来源有两类：
/// 1. 付款页签券失败：只有付款页内存里的"重试上传"（CashPaymentWorkflowService.RetryVoucherUploadAsync）会再次签发，
///    收银员离开付款页或重启程序后就再也不会签发。
/// 2. 卡退款走恢复流程落单：退款券 tender 由 AuthorizeRefundTenderAsync 离线生成（引用即占位串），之后的
///    Linkly / Square 卡退款若经 CardPaymentRecoveryService / SquarePaymentRecoveryService 恢复完成，恢复服务把
///    draft 里的 tender 与卡 tender 拼起来直接落单，全程不调用签券。这类订单从落库起就带着占位引用，
///    与签券是否失败无关；补签只能由负责付款流程/恢复服务的改动提供，上传侧不能也不应代签。
/// 暂扣最长 <see cref="OrderUploadRetryPolicy.VoucherRefundHoldMaxAge"/>：入队满 10 分钟仍未签券时写 Warning
/// （同步中心队列行同时写明原因，见 <see cref="ILocalOrderUploadRepository.MarkVoucherRefundHoldsAsync"/>），
/// 超过暂扣窗口后放行上传时再写一条 Warning（见 <see cref="ReportReleased"/>），两条都交人工补券跟进。
/// 节流：每笔订单在本进程内第 1、2、4、8… 次被观察到时写 Warning，15 秒一轮的上传不会刷屏。
/// </summary>
internal static class OrderUploadVoucherRefundHoldLog
{
    private const int MaxTrackedOrders = 2000;
    private static readonly ConcurrentDictionary<Guid, int> Observations = new();
    private static readonly ConcurrentDictionary<Guid, int> Releases = new();

    /// <summary>
    /// 暂扣窗口已过、订单仍带着占位引用被放行上传：总部会收到一笔退款券号缺失的退货，必须写 Warning 交人工补券。
    /// 上传失败后会重试，同一订单按 1、2、4、8… 次节流。
    /// </summary>
    internal static void ReportReleased(Guid orderGuid, DateTimeOffset? queuedAt)
    {
        if (Releases.Count >= MaxTrackedOrders && !Releases.ContainsKey(orderGuid))
        {
            Releases.Clear();
        }

        var release = Releases.AddOrUpdate(
            orderGuid,
            1,
            (_, count) => count == int.MaxValue ? count : count + 1);
        var message =
            $"upload released: refund voucher still not issued orderGuid={orderGuid:D} release={release} " +
            $"queuedAt={queuedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "<unknown>"} " +
            $"holdMaxMinutes={OrderUploadRetryPolicy.VoucherRefundHoldMaxAge.TotalMinutes:0} " +
            "退款券暂扣已超时仍未签发，订单带占位券号上传，总部这笔退货缺少退款券号，需人工补券跟进。";
        if (!OrderUploadFailureLogThrottle.ShouldReport(release))
        {
            ConsoleLog.Write("OrderSync", message);
            return;
        }

        ConsoleLog.WriteWarning(
            "OrderSync",
            message,
            new ApplicationLogContext(
                TraceId: orderGuid.ToString("D"),
                RequestPath: OrderSyncApiClient.RequestPath,
                RequestMethod: "POST",
                Properties: new Dictionary<string, object?>
                {
                    ["reason"] = "voucher-refund-hold-expired",
                    ["attemptCount"] = release
                }));
    }

    internal static void Report(Guid orderGuid, DateTimeOffset? queuedAt, string source)
    {
        if (Observations.Count >= MaxTrackedOrders && !Observations.ContainsKey(orderGuid))
        {
            Observations.Clear();
        }

        var observation = Observations.AddOrUpdate(
            orderGuid,
            1,
            (_, count) => count == int.MaxValue ? count : count + 1);
        var message =
            $"upload held: refund voucher not issued orderGuid={orderGuid:D} source={source} observation={observation} " +
            $"queuedAt={queuedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "<unknown>"} " +
            $"退款券尚未签发，订单暂不上传（最长 {OrderUploadRetryPolicy.VoucherRefundHoldMaxAge.TotalMinutes:0} 分钟后带占位券号上传）；" +
            "付款页未关闭时可在付款页重试签券，否则（含卡退款恢复落单的订单）需人工补签跟进。";
        if (!OrderUploadFailureLogThrottle.ShouldReport(observation))
        {
            ConsoleLog.Write("OrderSync", message);
            return;
        }

        ConsoleLog.WriteWarning(
            "OrderSync",
            message,
            new ApplicationLogContext(
                TraceId: orderGuid.ToString("D"),
                RequestPath: OrderSyncApiClient.RequestPath,
                RequestMethod: "POST",
                Properties: new Dictionary<string, object?>
                {
                    ["reason"] = "voucher-refund-pending",
                    ["attemptCount"] = observation
                }));
    }
}

/// <summary>后台扫描到的"退款券待签发"订单：入队时间用于判断是否已卡住超过告警阈值。</summary>
public sealed record OrderUploadVoucherRefundHold(Guid OrderGuid, DateTimeOffset QueuedAt);

public interface IOrderUploadExecutionService
{
    Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    /// <summary>后台自动上传：只取已到退避时间的待传/失败订单。</summary>
    Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同步中心"全部重试"：忽略退避时间，覆盖全部待传/失败订单（仍排除暂扣窗口内的退款券待签发订单）。
    /// 默认实现退回 <see cref="ExecutePendingAsync"/>，只为让既有测试替身无需改动即可编译。
    /// </summary>
    Task<OrderUploadExecutionResult> ExecuteAllRetryableAsync(
        int batchSize = OrderUploadExecutionService.RetryAllBatchSize,
        CancellationToken cancellationToken = default) =>
        ExecutePendingAsync(batchSize, cancellationToken);

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
    ILocalOrderUploadRepository uploadRepository,
    TimeProvider? timeProvider = null) : IOrderUploadService
{
    internal const string UploadTimedOutMessage =
        "Order upload timed out. The server may already have received it; the order stays queued and will be retried with the same order ID.";

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Log($"upload start orderGuid={orderGuid:D}");
        var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken)
            ?? throw new InvalidOperationException("Order was not found for upload.");
        if (order.Payments.Any(payment => OrderUploadRetryPolicy.IsVoucherRefundPendingReference(payment.Reference)))
        {
            // 关键逻辑：收银流程先把带占位引用的退款订单落库，再联网签券并回写真实券号。
            // 队列取单已排除暂扣窗口内的这类订单，这里兜住"选中重试/交易历史重传"等直接按订单号上传的入口，
            // 与取单用同一把尺子（队列入队时间；没有队列行时退回订单时间）：
            // 窗口内不改任何上传状态、不计失败次数，等签券回写后再正常上传；窗口外照旧上传并告警。
            var queuedAt = await uploadRepository.GetQueuedAtAsync(orderGuid, cancellationToken) ?? order.SoldAt;
            if (OrderUploadRetryPolicy.IsWithinVoucherRefundHold(queuedAt, clock.GetUtcNow()))
            {
                OrderUploadVoucherRefundHoldLog.Report(orderGuid, queuedAt, source: "upload-guard");
                throw new OrderUploadVoucherRefundPendingException(orderGuid);
            }

            OrderUploadVoucherRefundHoldLog.ReportReleased(orderGuid, queuedAt);
        }

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
        catch (CatalogApiException ex) when (OrderUploadRetryPolicy.IsGlobalAuthorizationFailure(ex))
        {
            // 设备授权失效或收银员票据缺失/过期不是订单失败：恢复 Pending，等待下一次有效登录；
            // 不计失败次数、不动退避时间（批量上传据此结束本轮，见 OrderUploadExecutionService）。
            // 403 DEVICE_SCOPE_FORBIDDEN 是这笔订单的门店/设备与当前授权不符，属单单问题，走下面的失败退避。
            await uploadRepository.MarkAuthorizationDeferredAsync(orderGuid, cancellationToken);
            Log($"upload deferred orderGuid={orderGuid:D} reason=cashier-authorization-required");
            throw new OrderUploadAuthorizationRequiredException("需要有效收银员授权后再上传订单。", ex);
        }
        catch (TaskCanceledException ex) when (
            !cancellationToken.IsCancellationRequested && ex.InnerException is TimeoutException)
        {
            // HttpClient 超时与端点代际取消都是 TaskCanceledException，只有超时带内层 TimeoutException。
            // 超时后服务器是否已收到未知：订单留在队列并写明原因，同一订单号重试由服务端去重；
            // 改抛非取消异常，让批量上传按"未完成"计数并继续下一笔，不再当作端点切换中断整批。
            // 超时也是暂时性失败：计一次尝试并退避，避免服务端持续不可用时每 15 秒都等满一次超时。
            await uploadRepository.MarkFailedWithBackoffAsync(
                orderGuid,
                UploadTimedOutMessage,
                OrderUploadFailureKind.Timeout,
                CancellationToken.None);
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
            // Accepted=false 是服务端在 200 里明确不收，与可识别的 4xx 一样按业务拒绝放宽退避封顶；其余按异常分类。
            var failureKind = notAcceptedLogged
                ? OrderUploadFailureKind.BusinessRejection
                : OrderUploadRetryPolicy.Classify(ex);
            await uploadRepository.MarkFailedWithBackoffAsync(orderGuid, ex.Message, failureKind, cancellationToken);
            Log(
                $"upload failed orderGuid={orderGuid:D} error={ex.GetType().Name} message={ex.Message} " +
                $"failureKind={failureKind} elapsedMs={stopwatch.ElapsedMilliseconds}");
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

    // 手动"全部重试"一次最多覆盖的订单数：与交易历史按日期重传的批量一致，远大于同步中心可见的 20 行，
    // 避免排在队首的旧失败单占满一批、后面的订单手动重试也轮不到。
    internal const int RetryAllBatchSize = 500;

    // 退款券签发正常几秒内完成；入队超过 10 分钟仍是占位引用，基本可判定付款页已放弃重试，
    // 或订单来自从不签券的卡退款恢复落单（见 OrderUploadVoucherRefundHoldLog），先告警交人工跟进；
    // 满 OrderUploadRetryPolicy.VoucherRefundHoldMaxAge 后放行上传。
    internal static readonly TimeSpan VoucherRefundHoldWarningAge = TimeSpan.FromMinutes(10);

    private const int VoucherRefundHoldScanLimit = 20;

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
                    // 授权失效是全局条件：当前与剩余订单都按"未完成"计数后结束本批，剩余订单不再逐笔请求、状态不动。
                    var unfinished = selected.Length - index;
                    failedCount += unfinished;
                    Log(
                        $"execute selected stopped orderGuid={orderGuid:D} reason=cashier-authorization-required " +
                        $"unfinished={unfinished}");
                    break;
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

    public Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default) =>
        ExecuteQueueAsync(ignoreBackoff: false, batchSize, cancellationToken);

    public Task<OrderUploadExecutionResult> ExecuteAllRetryableAsync(
        int batchSize = RetryAllBatchSize,
        CancellationToken cancellationToken = default) =>
        ExecuteQueueAsync(ignoreBackoff: true, batchSize, cancellationToken);

    /// <summary>
    /// 按队列取单并串行上传。自动上传（ignoreBackoff=false）只取已到退避时间的订单，
    /// 手动"全部重试"忽略退避；两者都排除暂扣窗口内的退款券待签发订单。
    /// </summary>
    private async Task<OrderUploadExecutionResult> ExecuteQueueAsync(
        bool ignoreBackoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var mode = ignoreBackoff ? "retry-all" : "due";
            Log($"execute pending start mode={mode} batchSize={batchSize}");
            await ReportVoucherRefundHoldsSafelyAsync(cancellationToken);
            var orderGuids = ignoreBackoff
                ? await uploadRepository.GetRetryableOrderGuidsAsync(batchSize, cancellationToken)
                : await uploadRepository.GetPendingOrderGuidsAsync(batchSize, cancellationToken);
            Log($"execute pending queued mode={mode} count={orderGuids.Count} batchSize={batchSize}");
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
                    // 关键逻辑：授权失效是全局条件，后面每一笔都会同样被拒。结束本轮，剩余订单原样留在队列，
                    // 不计失败次数、不改退避时间，等收银员重新登录后的下一轮自动上传。
                    var skipped = orderGuids.Count - index - 1;
                    Log(
                        $"execute pending stopped orderGuid={orderGuid:D} reason=cashier-authorization-required " +
                        $"skipped={skipped} elapsedMs={stopwatch.ElapsedMilliseconds}");
                    break;
                }
                catch (OrderUploadVoucherRefundPendingException)
                {
                    // 取单与签券回写之间的竞态兜底：这笔不算失败，等回写真实券号后的下一轮再传。
                    consecutiveTimeouts = 0;
                    Log($"execute pending item held orderGuid={orderGuid:D} reason=voucher-refund-pending");
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
                $"execute pending completed mode={mode} attempted={orderGuids.Count} uploaded={uploadedCount} failed={failedCount} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new OrderUploadExecutionResult(orderGuids.Count, uploadedCount, failedCount, wasInterrupted);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    /// <summary>
    /// 退款券待签发的订单被取单排除后不会再出现在上传日志里；入队超过阈值仍未签发的，在这里写进中心日志交人工跟进，
    /// 并在队列行写明暂扣原因，同步中心不再只显示一条没有原因的 Pending。
    /// 扫描失败只记本地日志，不影响本轮上传。
    /// </summary>
    private async Task ReportVoucherRefundHoldsSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var holds = await uploadRepository.GetVoucherRefundHeldOrdersAsync(
                VoucherRefundHoldWarningAge,
                VoucherRefundHoldScanLimit,
                cancellationToken);
            foreach (var hold in holds)
            {
                OrderUploadVoucherRefundHoldLog.Report(hold.OrderGuid, hold.QueuedAt, source: "queue-scan");
            }

            // 先告警再写原因：写库失败也不会吞掉上面的中心日志。只改 ErrorMessage，状态、失败次数与退避时间都不动。
            var marked = await uploadRepository.MarkVoucherRefundHoldsAsync(VoucherRefundHoldWarningAge, cancellationToken);
            if (marked > 0)
            {
                Log($"voucher refund hold reason recorded rows={marked}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"voucher refund hold scan failed error={ex.GetType().Name} message={ex.Message}");
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
    /// <summary>
    /// 后台自动上传取单：待传/失败订单中只取已到退避时间（NextAttemptAt 为空或已过）的，
    /// 排除暂扣窗口内的退款券待签发订单；从未失败过的新单优先，再按入队先后。
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动"全部重试"取单：忽略退避时间，仍排除暂扣窗口内的退款券待签发订单。
    /// 默认实现退回 <see cref="GetPendingOrderGuidsAsync"/>，只为让既有测试替身无需改动即可编译。
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRetryableOrderGuidsAsync(int take, CancellationToken cancellationToken = default) =>
        GetPendingOrderGuidsAsync(take, cancellationToken);

    /// <summary>
    /// 订单在上传队列里的入队时间（没有队列行时返回 null），供按订单号直接上传的入口与取单用同一把尺子判断暂扣窗口。
    /// 默认实现返回 null（调用方退回订单时间），只为让既有测试替身无需改动即可编译。
    /// </summary>
    Task<DateTimeOffset?> GetQueuedAtAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
        Task.FromResult<DateTimeOffset?>(null);

    /// <summary>入队超过 minimumAge、仍在暂扣窗口内且退款券未签发（占位引用）的待传/失败订单，供中心日志告警。</summary>
    Task<IReadOnlyList<OrderUploadVoucherRefundHold>> GetVoucherRefundHeldOrdersAsync(
        TimeSpan minimumAge,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OrderUploadVoucherRefundHold>>([]);

    /// <summary>
    /// 给入队超过 minimumAge、仍在暂扣窗口内且退款券未签发（占位引用）的待传/失败订单在队列行写明暂扣原因，供同步中心显示。
    /// 只改 ErrorMessage，Status / AttemptCount / NextAttemptAt / LastTriedAt 都保持原样；返回本次实际改写的行数。
    /// 默认实现什么都不做，只为让既有测试替身无需改动即可编译。
    /// </summary>
    Task<int> MarkVoucherRefundHoldsAsync(TimeSpan minimumAge, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    Task<IReadOnlyList<Guid>> GetReuploadableOrderGuidsAsync(
        DateTimeOffset soldFrom,
        DateTimeOffset soldTo,
        string? deviceCode,
        CancellationToken cancellationToken = default);

    Task MarkSyncingAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    /// <summary>立即重新排队（端点代际切换取消等）：不计失败次数，清空退避时间。</summary>
    Task MarkPendingAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    // 留在队列等重试，同时记下原因，供同步中心显示；与上面一样不计次数、清空退避时间。
    Task MarkPendingAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// 全局授权条件不满足（收银员票据缺失/过期、设备授权失效）：恢复 Pending，失败次数与退避时间都保持原样。
    /// 默认实现退回 <see cref="MarkPendingAsync(Guid, CancellationToken)"/>，只为让既有测试替身无需改动即可编译。
    /// </summary>
    Task MarkAuthorizationDeferredAsync(Guid orderGuid, CancellationToken cancellationToken = default) =>
        MarkPendingAsync(orderGuid, cancellationToken);

    Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记一次失败尝试并安排退避：AttemptCount+1，NextAttemptAt = 现在 + 按档位与新次数计算的延时。
    /// Timeout 保持 Pending（服务器是否已收到未知），其余置为 Failed。
    /// 默认实现退回旧的标记方法，只为让既有测试替身无需改动即可编译。
    /// </summary>
    Task MarkFailedWithBackoffAsync(
        Guid orderGuid,
        string errorMessage,
        OrderUploadFailureKind failureKind,
        CancellationToken cancellationToken = default) =>
        failureKind == OrderUploadFailureKind.Timeout
            ? MarkPendingAsync(orderGuid, errorMessage, cancellationToken)
            : MarkFailedAsync(orderGuid, errorMessage, cancellationToken);
}

public sealed class LocalOrderUploadRepository(LocalSqliteStore store, TimeProvider? timeProvider = null)
    : ILocalOrderUploadRepository
{
    private const int MaxPendingTake = 100;
    private const int MaxRetryableTake = 1000;

    // 关键逻辑：退款券待签发的订单在暂扣窗口内不能上传。收银流程先落库（付款引用为占位串）再联网签券并回写真实券号，
    // 服务端按订单号去重，带占位引用上传一次后，回写的券号就再也上不去了。
    // 暂扣只按入队时间持续 OrderUploadRetryPolicy.VoucherRefundHoldMaxAge：卡退款经恢复服务落单的订单从不签券，
    // 不能让它们永远留在本机；窗口过后照旧上传，由上传入口写 Warning 交人工补券（见 OrderUploadVoucherRefundHoldLog）。
    // 与 CashPaymentWorkflowService 的判断同口径：trim（含制表/换行）后大小写不敏感。
    // CreatedAt 是本地时间带偏移的 "O" 串（夏令时前后偏移不同），按时间比较必须用 julianday 换算，不能比字符串；
    // 时间条件写在前面，窗口外的行不必再查付款表。
    private const string VoucherRefundHoldActive = """
        julianday(q.CreatedAt) > julianday($HoldReleaseBefore)
          AND EXISTS (
                SELECT 1
                FROM LocalPayments p
                WHERE p.OrderGuid = q.EntityId
                  AND UPPER(TRIM(p.Reference, ' ' || char(9) || char(10) || char(13))) = 'VOUCHER_REFUND_PENDING'
            )
        """;

    // 取单 SQL 由 IX_SyncQueue_Status_EntityType_CreatedAt 定位活动行，占位引用检查走 IX_LocalPayments_OrderGuid。
    // NextAttemptAt 统一写成 UTC 的 "O" 串（固定 7 位小数、+00:00），与同格式的 $Now 按字符串比较即按时间先后比较。
    // 从未失败过的新单（AttemptCount=0）排在到期的重试单前面，新销售不会被一批反复失败的旧单拖慢。
    internal const string DueOrderGuidsSql = $"""
        SELECT q.EntityId
        FROM SyncQueue q
        WHERE q.Status IN ('Pending', 'Failed')
          AND q.EntityType = 'Order'
          AND (q.NextAttemptAt IS NULL OR q.NextAttemptAt <= $Now)
          AND NOT ({VoucherRefundHoldActive})
        ORDER BY CASE WHEN q.AttemptCount = 0 THEN 0 ELSE 1 END, q.CreatedAt, q.Id
        LIMIT $Take;
        """;

    internal const string RetryableOrderGuidsSql = $"""
        SELECT q.EntityId
        FROM SyncQueue q
        WHERE q.Status IN ('Pending', 'Failed')
          AND q.EntityType = 'Order'
          AND NOT ({VoucherRefundHoldActive})
        ORDER BY q.CreatedAt, q.Id
        LIMIT $Take;
        """;

    internal const string QueuedAtSql = """
        SELECT CreatedAt
        FROM SyncQueue
        WHERE EntityId = $OrderGuid AND EntityType = 'Order'
        ORDER BY Id DESC
        LIMIT 1;
        """;

    // 告警扫描：入队已满告警阈值、但仍在暂扣窗口内的订单。窗口外的订单下一轮就会被放行上传，由上传入口另行告警。
    internal const string VoucherRefundHeldOrdersSql = $"""
        SELECT q.EntityId, q.CreatedAt
        FROM SyncQueue q
        WHERE q.Status IN ('Pending', 'Failed')
          AND q.EntityType = 'Order'
          AND julianday(q.CreatedAt) <= julianday($QueuedBefore)
          AND {VoucherRefundHoldActive}
        ORDER BY q.CreatedAt, q.Id
        LIMIT $Take;
        """;

    // 同步中心显示的暂扣原因（与其他上传错误一样是英文原文），前缀保持 "Refund voucher not issued" 便于检索。
    internal const string VoucherRefundHeldErrorMessage =
        "Refund voucher not issued yet; upload is held for up to 30 minutes, then the order is uploaded without " +
        "the voucher number. Retry the voucher on the payment page or contact support.";

    // 与告警扫描同一筛选条件，但不限条数：暂扣中的订单都写明原因。只改 ErrorMessage，状态、失败次数、退避时间、
    // LastTriedAt 都不动；签券回写或窗口过后放行，下一轮上传进入 Syncing 时会清空这条原因。已是同一原因的行不再重写。
    // 只标窗口内的行：放行后若上传失败，队列行显示的应是真实的失败原因，不能被暂扣原因盖掉。
    internal const string MarkVoucherRefundHoldsSql = $"""
        UPDATE SyncQueue AS q
        SET ErrorMessage = $ErrorMessage
        WHERE q.Status IN ('Pending', 'Failed')
          AND q.EntityType = 'Order'
          AND julianday(q.CreatedAt) <= julianday($QueuedBefore)
          AND (q.ErrorMessage IS NULL OR q.ErrorMessage <> $ErrorMessage)
          AND {VoucherRefundHoldActive};
        """;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>写状态时对退避列（AttemptCount / NextAttemptAt）的处理方式。</summary>
    private enum BackoffUpdate
    {
        /// <summary>两列都不动：进入上传中、全局授权延后。</summary>
        Keep,

        /// <summary>不计次数、清空 NextAttemptAt：端点切换等立即重排队。</summary>
        ClearDue,

        /// <summary>两列清零：上传成功。</summary>
        Reset,

        /// <summary>次数 +1 并按退避策略设置 NextAttemptAt：上传失败。</summary>
        Schedule
    }

    public Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return QueryOrderGuidsAsync(
            DueOrderGuidsSql,
            Math.Clamp(take, 1, MaxPendingTake),
            now,
            includeNow: true,
            cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> GetRetryableOrderGuidsAsync(int take, CancellationToken cancellationToken = default)
    {
        return QueryOrderGuidsAsync(
            RetryableOrderGuidsSql,
            Math.Clamp(take, 1, MaxRetryableTake),
            clock.GetUtcNow(),
            includeNow: false,
            cancellationToken);
    }

    public async Task<DateTimeOffset?> GetQueuedAtAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = QueuedAtSql;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is string text &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var queuedAt)
                ? queuedAt
                : null;
    }

    public async Task<IReadOnlyList<OrderUploadVoucherRefundHold>> GetVoucherRefundHeldOrdersAsync(
        TimeSpan minimumAge,
        int take,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = VoucherRefundHeldOrdersSql;
        command.Parameters.AddWithValue("$QueuedBefore", FormatUtc(now - minimumAge));
        command.Parameters.AddWithValue("$HoldReleaseBefore", FormatHoldReleaseBefore(now));
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, MaxPendingTake));

        var holds = new List<OrderUploadVoucherRefundHold>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var orderGuid) &&
                DateTimeOffset.TryParse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var queuedAt))
            {
                holds.Add(new OrderUploadVoucherRefundHold(orderGuid, queuedAt));
            }
        }

        return holds;
    }

    public async Task<int> MarkVoucherRefundHoldsAsync(TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = MarkVoucherRefundHoldsSql;
        command.Parameters.AddWithValue("$ErrorMessage", VoucherRefundHeldErrorMessage);
        command.Parameters.AddWithValue("$QueuedBefore", FormatUtc(now - minimumAge));
        command.Parameters.AddWithValue("$HoldReleaseBefore", FormatHoldReleaseBefore(now));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> QueryOrderGuidsAsync(
        string sql,
        int take,
        DateTimeOffset now,
        bool includeNow,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$Take", take);
        command.Parameters.AddWithValue("$HoldReleaseBefore", FormatHoldReleaseBefore(now));
        if (includeNow)
        {
            command.Parameters.AddWithValue("$Now", FormatUtc(now));
        }

        // 每笔订单只入队一行；保险起见仍按首次出现去重并保持排序。
        var orderGuids = new List<Guid>();
        var seen = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var orderGuid) && seen.Add(orderGuid))
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
        return UpdateStatusAsync(orderGuid, "Syncing", null, BackoffUpdate.Keep, cancellationToken: cancellationToken);
    }

    public Task MarkPendingAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Pending", null, BackoffUpdate.ClearDue, cancellationToken: cancellationToken);
    }

    public Task MarkPendingAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Pending", errorMessage, BackoffUpdate.ClearDue, cancellationToken: cancellationToken);
    }

    public Task MarkAuthorizationDeferredAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Pending", null, BackoffUpdate.Keep, cancellationToken: cancellationToken);
    }

    public Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Synced", null, BackoffUpdate.Reset, cancellationToken: cancellationToken);
    }

    public Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default)
    {
        return MarkFailedWithBackoffAsync(orderGuid, errorMessage, OrderUploadFailureKind.Transient, cancellationToken);
    }

    public Task MarkFailedWithBackoffAsync(
        Guid orderGuid,
        string errorMessage,
        OrderUploadFailureKind failureKind,
        CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(
            orderGuid,
            failureKind == OrderUploadFailureKind.Timeout ? "Pending" : "Failed",
            errorMessage,
            BackoffUpdate.Schedule,
            failureKind,
            cancellationToken);
    }

    private async Task UpdateStatusAsync(
        Guid orderGuid,
        string status,
        string? errorMessage,
        BackoffUpdate backoff,
        OrderUploadFailureKind failureKind = OrderUploadFailureKind.Transient,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var now = clock.GetUtcNow();
        // LastTriedAt / CreatedAt 沿用既有写法（本地时间带偏移的 "O" 串），同步中心按它显示；
        // 与 NextAttemptAt 取自同一时刻，两者之差就是本次退避延时。
        var localNow = TimeZoneInfo.ConvertTime(now, clock.LocalTimeZone).ToString("O", CultureInfo.InvariantCulture);
        var orderGuidText = orderGuid.ToString();

        var attemptCount = 0;
        string? nextAttemptAt = null;
        if (backoff == BackoffUpdate.Schedule)
        {
            await using var readCommand = connection.CreateCommand();
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT AttemptCount
                FROM SyncQueue
                WHERE EntityId = $OrderGuid AND EntityType = 'Order'
                ORDER BY Id DESC
                LIMIT 1;
                """;
            readCommand.Parameters.AddWithValue("$OrderGuid", orderGuidText);
            var previous = await readCommand.ExecuteScalarAsync(cancellationToken) is { } value and not DBNull
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : 0;
            attemptCount = previous >= int.MaxValue - 1 ? int.MaxValue : Math.Max(previous, 0) + 1;
            nextAttemptAt = FormatUtc(now + OrderUploadRetryPolicy.GetRetryDelay(attemptCount, failureKind));
        }

        await using (var orderCommand = connection.CreateCommand())
        {
            orderCommand.Transaction = transaction;
            orderCommand.CommandText = "UPDATE LocalOrders SET SyncStatus = $Status WHERE OrderGuid = $OrderGuid;";
            orderCommand.Parameters.AddWithValue("$Status", status);
            orderCommand.Parameters.AddWithValue("$OrderGuid", orderGuidText);
            await orderCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int updatedQueueRows;
        await using (var queueCommand = connection.CreateCommand())
        {
            queueCommand.Transaction = transaction;
            var backoffAssignments = backoff switch
            {
                BackoffUpdate.ClearDue => ", NextAttemptAt = NULL",
                BackoffUpdate.Reset => ", AttemptCount = 0, NextAttemptAt = NULL",
                BackoffUpdate.Schedule => ", AttemptCount = $AttemptCount, NextAttemptAt = $NextAttemptAt",
                _ => string.Empty
            };
            queueCommand.CommandText = $"""
                UPDATE SyncQueue
                SET Status = $Status,
                    LastTriedAt = $LastTriedAt,
                    ErrorMessage = $ErrorMessage{backoffAssignments}
                WHERE EntityId = $OrderGuid AND EntityType = 'Order';
                """;
            queueCommand.Parameters.AddWithValue("$Status", status);
            queueCommand.Parameters.AddWithValue("$LastTriedAt", localNow);
            queueCommand.Parameters.AddWithValue("$ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            queueCommand.Parameters.AddWithValue("$OrderGuid", orderGuidText);
            if (backoff == BackoffUpdate.Schedule)
            {
                queueCommand.Parameters.AddWithValue("$AttemptCount", attemptCount);
                queueCommand.Parameters.AddWithValue("$NextAttemptAt", (object?)nextAttemptAt ?? DBNull.Value);
            }

            updatedQueueRows = await queueCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (updatedQueueRows == 0)
        {
            // 关键逻辑：启动时会清理 30 天前已同步的队列行（见 LocalSchemaService）。交易历史重传这类老订单时队列行已不存在，
            // 若不补回，失败后订单只剩 LocalOrders.SyncStatus=Failed，同步中心看不到、后台也不会再重试；
            // 上传途中进程退出还会让订单永远停在 Syncing（启动恢复要靠队列行）。只为本地确实存在的订单补行。
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO SyncQueue (EntityId, EntityType, Status, CreatedAt, LastTriedAt, ErrorMessage, AttemptCount, NextAttemptAt)
                SELECT $OrderGuid, 'Order', $Status, $LastTriedAt, $LastTriedAt, $ErrorMessage, $AttemptCount, $NextAttemptAt
                WHERE EXISTS (SELECT 1 FROM LocalOrders WHERE OrderGuid = $OrderGuid);
                """;
            insertCommand.Parameters.AddWithValue("$OrderGuid", orderGuidText);
            insertCommand.Parameters.AddWithValue("$Status", status);
            insertCommand.Parameters.AddWithValue("$LastTriedAt", localNow);
            insertCommand.Parameters.AddWithValue("$ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$AttemptCount", attemptCount);
            insertCommand.Parameters.AddWithValue("$NextAttemptAt", (object?)nextAttemptAt ?? DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 退避时间一律写成 UTC 的 ISO "O" 串（固定 7 位小数、+00:00），
    /// SQL 里按字符串比较"是否到期"与按时间先后比较结果一致（与日结上传的 FormatUtc 同口径）。
    /// </summary>
    internal static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    // 暂扣窗口的起点：入队时间晚于它的退款券待签发订单仍在暂扣中（SQL 里用 julianday 与本地带偏移的 CreatedAt 比较）。
    private static string FormatHoldReleaseBefore(DateTimeOffset now)
    {
        return FormatUtc(now - OrderUploadRetryPolicy.VoucherRefundHoldMaxAge);
    }
}

public sealed record OrderUploadExecutionResult(
    int AttemptedCount,
    int UploadedCount,
    int FailedCount,
    bool WasInterrupted = false);
