using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IReceiptReturnsWorkflowService
{
    Task<ReceiptReturnLookupResult> LookupOrderAsync(
        PosSessionState session,
        string orderQuery,
        CancellationToken cancellationToken = default);

    ReceiptReturnProductLookupResult LookupNoReceiptProduct(
        PosSessionState session,
        string productQuery);

    Task<ReceiptReturnProductLookupResult> LookupNoReceiptProductAsync(
        PosSessionState session,
        string productQuery,
        CancellationToken cancellationToken = default);

    ReceiptReturnPendingLineResult CreateNoReceiptOpenItem(
        PosSessionState session,
        string displayName,
        decimal unitPrice);

    IReadOnlyList<CartLine> AddReturnLinesToCart(
        IEnumerable<PendingReturnLine> lines,
        IReadOnlyList<OrderReturnPaymentCapacityDto>? paymentCapacities = null);
}

public sealed record ReceiptReturnLookupResult(
    ReceiptReturnOrder? Order,
    bool IsRemote,
    bool ReturnRecordsMayBeStale,
    string StatusMessage);

public sealed record ReceiptReturnProductLookupResult(
    SellableItemDto? Item,
    string StatusMessage);

public sealed record ReceiptReturnPendingLineResult(
    PendingReturnLine? Line,
    string StatusMessage);

public sealed record ReceiptReturnOrder(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal ActualAmount,
    IReadOnlyList<ReceiptReturnOrderLine> Lines,
    IReadOnlyList<OrderReturnRecordDto> ReturnRecords,
    IReadOnlyList<OrderReturnPaymentCapacityDto> PaymentCapacities);

public sealed record ReceiptReturnOrderLine(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    decimal OriginalQuantity,
    decimal UnitPrice,
    decimal OriginalActualAmount,
    decimal ReturnedQuantity)
{
    public decimal AvailableQuantity => Math.Max(0m, OriginalQuantity - ReturnedQuantity);

    public decimal ReturnUnitAmount => OriginalQuantity <= 0m
        ? UnitPrice
        : decimal.Round(OriginalActualAmount / OriginalQuantity, 2, MidpointRounding.AwayFromZero);
}

public sealed record PendingReturnLine(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    string ReturnSourceKey,
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderLineGuid);

public sealed class ReceiptReturnsWorkflowService(
    IReceiptQueryService receiptQueryService,
    ILocalOrderRepository localOrderRepository,
    IRemoteOrderHistoryService? remoteOrderHistoryService,
    LocalSellableItemIndex priceIndex,
    PosCartService cart,
    ILocalizationService? localization = null,
    IUiPriorityCoordinator? uiPriorityCoordinator = null) : IReceiptReturnsWorkflowService
{
    private const string OpenItemLookupCode = "OPENITEM";
    internal static readonly TimeSpan RemoteLookupTimeout = TimeSpan.FromSeconds(2);
    private readonly IUiPriorityCoordinator _uiPriorityCoordinator = uiPriorityCoordinator ?? UiPriorityCoordinator.Noop;

    public async Task<ReceiptReturnLookupResult> LookupOrderAsync(
        PosSessionState session,
        string orderQuery,
        CancellationToken cancellationToken = default)
    {
        var query = NormalizeQuery(orderQuery);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ReceiptReturnLookupResult(null, false, false, T("returns.status.lookupPrompt", "Scan or enter an order number."));
        }

        // 退货订单查询是收银员正在等待的前台操作；查询期间让目录同步在分页边界暂停，避免争抢同一 API/数据库。
        using var uiOperation = _uiPriorityCoordinator.BeginUiOperation("receipt-return-order-lookup");
        var stopwatch = Stopwatch.StartNew();
        var queryType = TryParseOrderGuid(query, out _) ? "guid" : "keyword";
        ConsoleLog.Write(
            "ReceiptReturns",
            $"lookup started store={session.StoreCode} online={session.IsOnline} " +
            $"queryType={queryType} queryLength={query.Length}");

        if (session.IsOnline && remoteOrderHistoryService is not null)
        {
            using var remoteLookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            remoteLookupCancellation.CancelAfter(RemoteLookupTimeout);
            var remoteCancellationToken = remoteLookupCancellation.Token;
            try
            {
                var remoteOrderGuid = await ResolveRemoteOrderGuidAsync(session, query, remoteCancellationToken);
                if (remoteOrderGuid is not null)
                {
                    var context = await remoteOrderHistoryService.GetReturnContextAsync(remoteOrderGuid.Value, remoteCancellationToken);
                    if (context is not null)
                    {
                        var result = new ReceiptReturnLookupResult(
                            MapRemote(context),
                            true,
                            false,
                            T("returns.status.loadedOnline", "Loaded online order and return records."));
                        LogLookupCompleted(result, queryType, query.Length, stopwatch.ElapsedMilliseconds);
                        return result;
                    }
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var result = new ReceiptReturnLookupResult(
                    null,
                    false,
                    false,
                    T("returns.status.lookupTimedOut", "Online order lookup timed out. Please retry."));
                // 自建 2 秒超时触发（收银员未取消）：Warning 并写明 reason=timeout。
                ConsoleLog.WriteWarning(
                    "ReceiptReturns",
                    $"lookup timed-out queryType={queryType} queryLength={query.Length} reason=timeout " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}",
                    new ApplicationLogContext(Properties: new Dictionary<string, object?>
                    {
                        ["storeCode"] = session.StoreCode,
                        ["deviceCode"] = session.DeviceCode,
                        ["reason"] = "timeout",
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                    }),
                    ex);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var fallback = await LookupLocalOrderAsync(session, query, cancellationToken);
                var result = fallback.Order is null
                    ? new ReceiptReturnLookupResult(null, false, true, Format("returns.status.lookupFailed", "Online order lookup failed: {0}", ex.Message))
                    : fallback with
                    {
                        ReturnRecordsMayBeStale = true,
                        StatusMessage = Format("returns.status.loadedLocalStaleWithError", "Loaded local order; online return records may be stale. {0}", ex.Message)
                    };
                // 联网失败（API/断网）已由订单历史客户端记 Warning，这里只记明走了本地降级路径（Information）；
                // 其它异常（映射、本地故障）客户端看不到，升级为 Warning 并带异常。
                var fallbackMessage =
                    $"lookup remote-failed fallback=local queryType={queryType} queryLength={query.Length} " +
                    $"fallbackFound={result.Order is not null} error={ex.GetType().Name} message={ex.Message} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}";
                var fallbackContext = new ApplicationLogContext(
                        StatusCode: ex is CatalogApiException { StatusCode: { } status } ? (int)status : null,
                        Properties: new Dictionary<string, object?>
                        {
                            ["storeCode"] = session.StoreCode,
                            ["deviceCode"] = session.DeviceCode,
                            ["mode"] = "local-fallback",
                            ["errorCode"] = (ex as CatalogApiException)?.ErrorCode,
                            ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                        });
                if (ex is CatalogApiException or HttpRequestException)
                {
                    ConsoleLog.WriteInformation("ReceiptReturns", fallbackMessage, fallbackContext);
                }
                else
                {
                    ConsoleLog.WriteWarning("ReceiptReturns", fallbackMessage, fallbackContext, ex);
                }

                return result;
            }
        }

        var localResult = await LookupLocalOrderAsync(session, query, cancellationToken);
        LogLookupCompleted(localResult, queryType, query.Length, stopwatch.ElapsedMilliseconds);
        return localResult;
    }

    public ReceiptReturnProductLookupResult LookupNoReceiptProduct(
        PosSessionState session,
        string productQuery)
    {
        var query = NormalizeQuery(productQuery);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ReceiptReturnProductLookupResult(null, T("returns.status.scanProduct", "Scan a product barcode."));
        }

        var exactMatches = priceIndex.FindExactMatches(session.StoreCode, query);
        var matches = exactMatches.Count > 0 ? exactMatches : priceIndex.Search(session.StoreCode, query, 8);
        return CreateNoReceiptProductLookupResult(matches);
    }

    public async Task<ReceiptReturnProductLookupResult> LookupNoReceiptProductAsync(
        PosSessionState session,
        string productQuery,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = NormalizeQuery(productQuery);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ReceiptReturnProductLookupResult(null, T("returns.status.scanProduct", "Scan a product barcode."));
        }

        // 无小票退货从 UI 线程进入时，精确命中也必须可取消并在后台完成。
        var exactMatches = await priceIndex.FindExactMatchesAsync(session.StoreCode, query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = exactMatches.Count > 0
            ? exactMatches
            : await priceIndex.SearchAsync(session.StoreCode, query, cancellationToken, 8);
        cancellationToken.ThrowIfCancellationRequested();
        return CreateNoReceiptProductLookupResult(matches);
    }

    private ReceiptReturnProductLookupResult CreateNoReceiptProductLookupResult(
        IReadOnlyList<SellableItemDto> matches)
    {
        var item = matches.FirstOrDefault();
        return item is null
            ? new ReceiptReturnProductLookupResult(null, T("returns.status.productNotFound", "Product was not found."))
            : new ReceiptReturnProductLookupResult(item, Format("returns.status.addedNoReceipt", "Added no-receipt return item: {0}", item.DisplayName));
    }

    public ReceiptReturnPendingLineResult CreateNoReceiptOpenItem(
        PosSessionState session,
        string displayName,
        decimal unitPrice)
    {
        var normalizedName = NormalizeQuery(displayName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return new ReceiptReturnPendingLineResult(null, T("returns.status.openItemNameRequired", "Enter an item name."));
        }

        if (unitPrice <= 0m)
        {
            return new ReceiptReturnPendingLineResult(null, T("returns.status.openItemPriceRequired", "Enter a retail price greater than zero."));
        }

        var matches = priceIndex.FindExactMatches(session.StoreCode, OpenItemLookupCode);
        if (matches.Count == 0)
        {
            return new ReceiptReturnPendingLineResult(null, T("returns.status.openItemMissing", "OPENITEM was not found in the local catalog."));
        }

        if (matches.Count > 1)
        {
            return new ReceiptReturnPendingLineResult(null, T("returns.status.openItemDuplicate", "Multiple OPENITEM records were found in the local catalog."));
        }

        var item = matches[0];
        var line = new PendingReturnLine(
            item.StoreCode,
            item.ProductCode,
            item.ReferenceCode,
            normalizedName,
            item.LookupCode,
            item.ItemNumber,
            item.ProductImage,
            1m,
            unitPrice,
            item.PriceSource,
            item.PriceSourceLabel,
            $"noreceipt-open:{item.StoreCode}:{Guid.NewGuid():N}",
            null,
            null);

        return new ReceiptReturnPendingLineResult(
            line,
            Format("returns.status.addedNoReceiptOpenItem", "Added no-barcode return item: {0}", normalizedName));
    }

    public IReadOnlyList<CartLine> AddReturnLinesToCart(
        IEnumerable<PendingReturnLine> lines,
        IReadOnlyList<OrderReturnPaymentCapacityDto>? paymentCapacities = null)
    {
        var added = new List<CartLine>();
        foreach (var pending in lines)
        {
            added.Add(cart.AddReturnLine(new ReturnCartLineRequest(
                pending.StoreCode,
                pending.ProductCode,
                pending.ReferenceCode,
                pending.DisplayName,
                pending.LookupCode,
                pending.ItemNumber,
                pending.ProductImage,
                pending.Quantity,
                pending.UnitPrice,
                pending.PriceSource,
                pending.PriceSourceLabel,
                pending.ReturnSourceKey,
                pending.OriginalOrderGuid,
                pending.OriginalOrderLineGuid)));
        }

        cart.AddReturnPaymentCapacities(paymentCapacities ?? []);
        LogReturnPaymentCapacities("return cart capacities added", paymentCapacities ?? []);
        return added;
    }

    private async Task<Guid?> ResolveRemoteOrderGuidAsync(
        PosSessionState session,
        string query,
        CancellationToken cancellationToken)
    {
        if (TryParseOrderGuid(query, out var orderGuid))
        {
            return orderGuid;
        }

        if (remoteOrderHistoryService is null)
        {
            return null;
        }

        var result = await remoteOrderHistoryService.QueryAsync(
            new RemoteOrderHistoryQuery(
                session.StoreCode,
                SoldFrom: null,
                SoldTo: null,
                DeviceCode: null,
                Keyword: query,
                Take: 1),
            cancellationToken);
        return result.Orders.FirstOrDefault()?.OrderGuid;
    }

    private async Task<ReceiptReturnLookupResult> LookupLocalOrderAsync(
        PosSessionState session,
        string query,
        CancellationToken cancellationToken)
    {
        LocalOrder? order = null;
        if (TryParseOrderGuid(query, out var orderGuid))
        {
            order = await localOrderRepository.GetOrderAsync(orderGuid, cancellationToken);
        }

        if (order is null)
        {
            var summaries = await receiptQueryService.GetRecentOrdersAsync(
                new LocalOrderHistoryQuery(
                    DeviceCode: null,
                    Keyword: query),
                1,
                cancellationToken);
            var summary = summaries.FirstOrDefault(summary => string.Equals(summary.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase))
                ?? summaries.FirstOrDefault();
            if (summary is not null)
            {
                order = await localOrderRepository.GetOrderAsync(summary.OrderGuid, cancellationToken);
            }
        }

        return order is null
            ? new ReceiptReturnLookupResult(null, false, false, T("returns.status.orderNotFound", "Order was not found."))
            : new ReceiptReturnLookupResult(
                MapLocal(order),
                false,
                true,
                T("returns.status.loadedLocalStale", "Loaded local order; return records may be stale."));
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        return string.Format(
            localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            localization?.T(key) ?? fallback,
            args);
    }

    private static ReceiptReturnOrder MapRemote(OrderReturnContextDto context)
    {
        var returnedByLine = context.ReturnRecords
            .Where(record => record.OriginalOrderDetailGuid is not null)
            .GroupBy(record => record.OriginalOrderDetailGuid!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(record => record.ReturnQuantity));

        return new ReceiptReturnOrder(
            context.Order.OrderGuid,
            context.Order.StoreCode,
            context.Order.DeviceCode,
            context.Order.CashierName,
            context.Order.SoldAt,
            context.Order.ActualAmount,
            context.Order.Lines.Select(line => new ReceiptReturnOrderLine(
                line.OrderLineGuid,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.ActualAmount,
                returnedByLine.TryGetValue(line.OrderLineGuid, out var returnedQuantity) ? returnedQuantity : 0m)).ToList(),
            context.ReturnRecords,
            context.PaymentCapacities ?? []);
    }

    private static ReceiptReturnOrder MapLocal(LocalOrder order)
    {
        return new ReceiptReturnOrder(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.ActualAmount,
            order.Lines.Select(line => new ReceiptReturnOrderLine(
                line.OrderLineGuid,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.ActualAmount,
                0m)).ToList(),
            [],
            BuildPaymentCapacities(order));
    }

    private static IReadOnlyList<OrderReturnPaymentCapacityDto> BuildPaymentCapacities(LocalOrder order)
    {
        return order.Payments
            .Where(payment => payment.Amount > 0m)
            .GroupBy(payment => new
            {
                payment.Method,
                payment.Reference
            })
            .Select(group => new OrderReturnPaymentCapacityDto(
                group.Key.Method,
                group.Sum(payment => payment.Amount),
                0m,
                group.Sum(payment => payment.Amount),
                group.Key.Reference,
                group.Key.Method == PaymentMethodKind.Card
                    ? group.SelectMany(payment => payment.CardTransactions ?? []).ToList()
                    : null,
                OriginalOrderGuid: order.OrderGuid))
            .ToList();
    }

    private static void LogReturnPaymentCapacities(
        string prefix,
        IReadOnlyList<OrderReturnPaymentCapacityDto> capacities)
    {
        foreach (var capacity in capacities.Where(capacity => capacity.Method == PaymentMethodKind.Card))
        {
            var refundReferences = string.Join(
                ',',
                (capacity.CardTransactions ?? [])
                    .Select(transaction => transaction.RefundReference)
                    .Where(reference => !string.IsNullOrWhiteSpace(reference))
                    .Select(reference => reference!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            ConsoleLog.Write(
                "CardRefund",
                $"{prefix} method={capacity.Method} originalOrder={capacity.OriginalOrderGuid?.ToString() ?? "<null>"} " +
                $"remaining={capacity.RemainingAmount:0.00} reference={LogValue(capacity.Reference)} " +
                $"cardTxCount={capacity.CardTransactions?.Count ?? 0} refundReferences={LogValue(refundReferences)}");
        }
    }

    private static void LogLookupCompleted(
        ReceiptReturnLookupResult result,
        string queryType,
        int queryLength,
        long elapsedMilliseconds)
    {
        ConsoleLog.Write(
            "ReceiptReturns",
            $"lookup completed queryType={queryType} queryLength={queryLength} " +
            $"source={(result.IsRemote ? "remote" : "local")} found={result.Order is not null} " +
            $"stale={result.ReturnRecordsMayBeStale} elapsedMs={elapsedMilliseconds}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string NormalizeQuery(string? query)
    {
        return query?.Trim() ?? string.Empty;
    }

    private static bool TryParseOrderGuid(string query, out Guid orderGuid)
    {
        return Guid.TryParse(query, out orderGuid);
    }
}
