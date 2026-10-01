using System.Diagnostics;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 收银端操作日志事件代码。固定值由后端和 Web 共同解释，禁止在调用点临时拼接。
/// </summary>
internal static class OperationAuditTypes
{
    public const string CashierLogin = "CASHIER_LOGIN";
    public const string CashierLogout = "CASHIER_LOGOUT";
    public const string CartItemAdd = "CART_ITEM_ADD";
    public const string CartItemRemove = "CART_ITEM_REMOVE";
    public const string CartItemQuantityChange = "CART_ITEM_QUANTITY_CHANGE";
    public const string CartItemPriceChange = "CART_ITEM_PRICE_CHANGE";
    public const string CartLineDiscountChange = "CART_LINE_DISCOUNT_CHANGE";
    public const string CartOrderDiscountChange = "CART_ORDER_DISCOUNT_CHANGE";
    public const string CartClear = "CART_CLEAR";
    public const string OrderHold = "ORDER_HOLD";
    public const string OrderRecall = "ORDER_RECALL";
    public const string OrderCancel = "ORDER_CANCEL";
    public const string CashDrawerOpen = "CASH_DRAWER_OPEN";
    public const string PaymentTenderAdd = "PAYMENT_TENDER_ADD";
    public const string PaymentTenderRemove = "PAYMENT_TENDER_REMOVE";
    public const string PaymentCancel = "PAYMENT_CANCEL";
    public const string SaleComplete = "SALE_COMPLETE";
    public const string ReturnRefundComplete = "RETURN_REFUND_COMPLETE";
    public const string SaleVoid = "SALE_VOID";
    public const string ReceiptReprint = "RECEIPT_REPRINT";
    public const string InstallmentRepaymentComplete = "INSTALLMENT_REPAYMENT_COMPLETE";
    public const string InstallmentRepaymentCancel = "INSTALLMENT_REPAYMENT_CANCEL";
    public const string DailyCloseSave = "DAILY_CLOSE_SAVE";
    public const string DailyCloseReprint = "DAILY_CLOSE_REPRINT";
    public const string LinklySettlement = "LINKLY_SETTLEMENT";
    public const string LinklySettlementReprint = "LINKLY_SETTLEMENT_REPRINT";
    public const string PermissionOverride = "PERMISSION_OVERRIDE";
    public const string InstallmentPickupConfirm = "INSTALLMENT_PICKUP_CONFIRM";
    public const string CatalogReset = "CATALOG_RESET";
    public const string TestSalesDataReset = "TEST_SALES_DATA_RESET";
    public const string DeviceReregister = "DEVICE_REREGISTER";
    public const string ApiServerChange = "API_SERVER_CHANGE";
    public const string RemoteMaintenanceInstall = "REMOTE_MAINTENANCE_INSTALL";
}

internal sealed record OperationAuditCartSnapshot(
    decimal Gross,
    decimal Discount,
    decimal Actual,
    IReadOnlyList<OperationAuditCartLineSnapshot> Lines);

internal sealed record OperationAuditCartLineSnapshot(
    string Key,
    string ProductCode,
    string? ItemNumber,
    string? ReferenceCode,
    string LookupCode,
    string DisplayName,
    string LineKind,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal Gross,
    decimal Actual);

internal static class OperationAuditEvents
{
    public static void RecordPermissionOverride(
        IOperationAuditLogger logger,
        PosSessionState requestingSession,
        Hbpos.Contracts.Cashiers.CashierSessionDto? authorizingSession,
        string permissionCode,
        string screen,
        string action,
        string outcome,
        string? reasonCode,
        string authorizationMode)
    {
        var auditEvent = CreateBase(
            OperationAuditTypes.PermissionOverride,
            outcome,
            requestingSession,
            correlationId: null,
            traceId: null);
        auditEvent.Properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestingCashierId"] = requestingSession.CashierSession?.CashierId ?? requestingSession.CashierId,
            ["authorizingCashierId"] = authorizingSession?.CashierId,
            ["authorizingUserGuid"] = authorizingSession?.UserGuid,
            ["permissionCode"] = permissionCode,
            ["authorizationMode"] = authorizationMode,
            ["screen"] = screen,
            ["action"] = action
        };
        auditEvent.ReasonCode = reasonCode;
        Record(logger, auditEvent);
    }

    public static OperationAuditCartSnapshot CaptureCart(IReadOnlyList<CartLine> lines)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var snapshots = new List<OperationAuditCartLineSnapshot>(lines.Count);
        foreach (var line in lines)
        {
            var baseKey = string.Join('|', line.ProductCode, line.ReferenceCode, line.LookupCode, line.Kind);
            var occurrence = occurrences.TryGetValue(baseKey, out var count) ? count : 0;
            occurrences[baseKey] = occurrence + 1;
            snapshots.Add(new OperationAuditCartLineSnapshot(
                $"{baseKey}|{occurrence}",
                line.ProductCode,
                line.ItemNumber,
                line.ReferenceCode,
                line.LookupCode,
                line.DisplayName,
                line.Kind.ToString(),
                RoundQuantity(line.SignedQuantity),
                RoundMoney(line.UnitPrice),
                RoundMoney(line.DiscountAmount),
                RoundMoney(line.GrossAmount),
                RoundMoney(line.ActualAmount)));
        }

        return new OperationAuditCartSnapshot(
            RoundMoney(lines.Sum(line => line.GrossAmount)),
            RoundMoney(lines.Sum(line => line.DiscountAmount)),
            RoundMoney(lines.Sum(line => line.ActualAmount)),
            snapshots);
    }

    public static OperationAuditCartSnapshot CaptureSuspendedOrder(SuspendedOrder order)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var snapshots = new List<OperationAuditCartLineSnapshot>(order.Lines.Count);
        foreach (var line in order.Lines)
        {
            var baseKey = string.Join('|', line.ProductCode, line.ReferenceCode, line.LookupCode, line.Kind);
            var occurrence = occurrences.TryGetValue(baseKey, out var count) ? count : 0;
            occurrences[baseKey] = occurrence + 1;
            var sign = line.Kind == CartLineKind.Return ? -1m : 1m;
            snapshots.Add(new OperationAuditCartLineSnapshot(
                $"{baseKey}|{occurrence}",
                line.ProductCode,
                line.ItemNumber,
                line.ReferenceCode,
                line.LookupCode,
                line.DisplayName,
                line.Kind.ToString(),
                RoundQuantity(sign * line.Quantity),
                RoundMoney(line.UnitPrice),
                RoundMoney(line.DiscountAmount),
                RoundMoney(sign * line.Quantity * line.UnitPrice),
                RoundMoney(line.ActualAmount)));
        }

        return new OperationAuditCartSnapshot(
            RoundMoney(order.TotalAmount),
            RoundMoney(order.DiscountAmount),
            RoundMoney(order.ActualAmount),
            snapshots);
    }

    public static bool HasChanged(OperationAuditCartSnapshot before, OperationAuditCartSnapshot after)
    {
        return before.Gross != after.Gross ||
            before.Discount != after.Discount ||
            before.Actual != after.Actual ||
            !before.Lines.SequenceEqual(after.Lines);
    }

    public static void RecordCartChange(
        IOperationAuditLogger? logger,
        string operationType,
        PosSessionState session,
        OperationAuditCartSnapshot before,
        OperationAuditCartSnapshot after,
        string outcome = "Succeeded",
        string? reasonCode = null,
        string? safeMessage = null,
        string? paymentMethod = null,
        decimal? paymentAmount = null,
        string? orderGuid = null,
        string? receiptNumber = null,
        string? correlationId = null,
        string? traceId = null)
    {
        if (logger is null)
        {
            return;
        }

        var beforeByKey = before.Lines.ToDictionary(line => line.Key, StringComparer.Ordinal);
        var afterByKey = after.Lines.ToDictionary(line => line.Key, StringComparer.Ordinal);
        var itemKeys = beforeByKey.Keys.Union(afterByKey.Keys, StringComparer.Ordinal);
        var items = new List<OperationAuditItemDto>();
        foreach (var key in itemKeys)
        {
            beforeByKey.TryGetValue(key, out var beforeLine);
            afterByKey.TryGetValue(key, out var afterLine);
            if (Equals(beforeLine, afterLine))
            {
                continue;
            }

            var identity = afterLine ?? beforeLine!;
            items.Add(new OperationAuditItemDto
            {
                ProductCode = identity.ProductCode,
                ItemNumber = identity.ItemNumber,
                ReferenceCode = identity.ReferenceCode,
                LookupCode = identity.LookupCode,
                DisplayName = identity.DisplayName,
                LineKind = identity.LineKind,
                BeforeQuantity = beforeLine?.Quantity ?? 0m,
                AfterQuantity = afterLine?.Quantity ?? 0m,
                QuantityDelta = RoundQuantity((afterLine?.Quantity ?? 0m) - (beforeLine?.Quantity ?? 0m)),
                BeforeUnitPrice = beforeLine?.UnitPrice ?? 0m,
                AfterUnitPrice = afterLine?.UnitPrice ?? 0m,
                UnitPriceDelta = RoundMoney((afterLine?.UnitPrice ?? 0m) - (beforeLine?.UnitPrice ?? 0m)),
                BeforeDiscountAmount = beforeLine?.Discount ?? 0m,
                AfterDiscountAmount = afterLine?.Discount ?? 0m,
                DiscountAmountDelta = RoundMoney((afterLine?.Discount ?? 0m) - (beforeLine?.Discount ?? 0m)),
                BeforeGrossAmount = beforeLine?.Gross ?? 0m,
                AfterGrossAmount = afterLine?.Gross ?? 0m,
                GrossAmountDelta = RoundMoney((afterLine?.Gross ?? 0m) - (beforeLine?.Gross ?? 0m)),
                BeforeActualAmount = beforeLine?.Actual ?? 0m,
                AfterActualAmount = afterLine?.Actual ?? 0m,
                ActualAmountDelta = RoundMoney((afterLine?.Actual ?? 0m) - (beforeLine?.Actual ?? 0m))
            });
        }

        var auditEvent = CreateBase(operationType, outcome, session, correlationId, traceId);
        auditEvent.BeforeGross = before.Gross;
        auditEvent.AfterGross = after.Gross;
        auditEvent.BeforeDiscount = before.Discount;
        auditEvent.AfterDiscount = after.Discount;
        auditEvent.BeforeActual = before.Actual;
        auditEvent.AfterActual = after.Actual;
        auditEvent.AmountDelta = RoundMoney(after.Actual - before.Actual);
        auditEvent.ReasonCode = reasonCode;
        auditEvent.SafeMessage = safeMessage;
        auditEvent.PaymentMethod = paymentMethod;
        auditEvent.PaymentAmount = paymentAmount is null ? null : RoundMoney(paymentAmount.Value);
        auditEvent.OrderGuid = orderGuid;
        auditEvent.ReceiptNumber = receiptNumber;
        auditEvent.Items = items;
        Record(logger, auditEvent);
    }

    public static void RecordAction(
        IOperationAuditLogger? logger,
        string operationType,
        string outcome,
        PosSessionState session,
        OperationAuditCartSnapshot? cart = null,
        string? reasonCode = null,
        string? safeMessage = null,
        string? paymentMethod = null,
        decimal? paymentAmount = null,
        string? orderGuid = null,
        string? receiptNumber = null,
        string? correlationId = null,
        string? traceId = null)
    {
        if (logger is null)
        {
            return;
        }

        var auditEvent = CreateBase(operationType, outcome, session, correlationId, traceId);
        auditEvent.ReasonCode = reasonCode;
        auditEvent.SafeMessage = safeMessage;
        auditEvent.PaymentMethod = paymentMethod;
        auditEvent.PaymentAmount = paymentAmount is null ? null : RoundMoney(paymentAmount.Value);
        auditEvent.OrderGuid = orderGuid;
        auditEvent.ReceiptNumber = receiptNumber;
        if (cart is not null)
        {
            auditEvent.BeforeGross = cart.Gross;
            auditEvent.AfterGross = cart.Gross;
            auditEvent.BeforeDiscount = cart.Discount;
            auditEvent.AfterDiscount = cart.Discount;
            auditEvent.BeforeActual = cart.Actual;
            auditEvent.AfterActual = cart.Actual;
            auditEvent.AmountDelta = 0m;
            auditEvent.Items = cart.Lines.Select(line => new OperationAuditItemDto
            {
                ProductCode = line.ProductCode,
                ItemNumber = line.ItemNumber,
                ReferenceCode = line.ReferenceCode,
                LookupCode = line.LookupCode,
                DisplayName = line.DisplayName,
                LineKind = line.LineKind,
                BeforeQuantity = line.Quantity,
                AfterQuantity = line.Quantity,
                QuantityDelta = 0m,
                BeforeUnitPrice = line.UnitPrice,
                AfterUnitPrice = line.UnitPrice,
                UnitPriceDelta = 0m,
                BeforeDiscountAmount = line.Discount,
                AfterDiscountAmount = line.Discount,
                DiscountAmountDelta = 0m,
                BeforeGrossAmount = line.Gross,
                AfterGrossAmount = line.Gross,
                GrossAmountDelta = 0m,
                BeforeActualAmount = line.Actual,
                AfterActualAmount = line.Actual,
                ActualAmountDelta = 0m
            }).ToList();
        }

        Record(logger, auditEvent);
    }

    /// <summary>
    /// 日结保存审计：系统应有现金记为改前实收、实点现金记为改后实收、差异记为金额变化，
    /// 使后台操作日志列表的“金额变化”列直接显示日结长短款并可按差异排序。
    /// </summary>
    public static void RecordDailyCloseSave(
        IOperationAuditLogger? logger,
        PosSessionState session,
        Guid dailyCloseGuid,
        decimal expectedCashAmount,
        decimal countedCashAmount,
        decimal cashDifference,
        string? correlationId = null,
        string? traceId = null)
    {
        if (logger is null)
        {
            return;
        }

        var auditEvent = CreateBase(OperationAuditTypes.DailyCloseSave, "Succeeded", session, correlationId, traceId);
        auditEvent.ReasonCode = "SAVED";
        auditEvent.OrderGuid = dailyCloseGuid.ToString("D");
        auditEvent.PaymentMethod = "Cash";
        auditEvent.BeforeActual = RoundMoney(expectedCashAmount);
        auditEvent.AfterActual = RoundMoney(countedCashAmount);
        auditEvent.AmountDelta = RoundMoney(cashDifference);
        Record(logger, auditEvent);
    }

    /// <summary>
    /// 记录不依赖 POS 会话的系统级操作（如切换服务器地址）：操作人由调用方在会话被清空前捕获，
    /// 门店与终端由写入器按记录时的设备授权补齐。
    /// </summary>
    public static void RecordActorAction(
        IOperationAuditLogger? logger,
        string operationType,
        string outcome,
        Hbpos.Contracts.Cashiers.CashierSessionDto? actor,
        string? reasonCode = null,
        string? safeMessage = null)
    {
        if (logger is null)
        {
            return;
        }

        Record(logger, new OperationAuditEventDto
        {
            EventId = Guid.NewGuid(),
            SchemaVersion = 1,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            OperationType = operationType,
            Outcome = outcome,
            CashierId = actor?.CashierId,
            UserGuid = actor?.UserGuid,
            CashierName = actor?.CashierName,
            IsOfflineCached = actor?.IsOfflineCached == true,
            IsEmergencyOverride = actor?.IsEmergencyOverride == true,
            CurrencyCode = "AUD",
            ReasonCode = reasonCode,
            SafeMessage = safeMessage
        });
    }

    public static (string CorrelationId, string TraceId) CreateCorrelation()
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrWhiteSpace(traceId))
        {
            traceId = Guid.NewGuid().ToString("N");
        }

        return (Guid.NewGuid().ToString("D"), traceId);
    }

    private static OperationAuditEventDto CreateBase(
        string operationType,
        string outcome,
        PosSessionState session,
        string? correlationId,
        string? traceId)
    {
        var cashier = session.CashierSession;
        var auditEvent = new OperationAuditEventDto
        {
            EventId = Guid.NewGuid(),
            SchemaVersion = 1,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            OperationType = operationType,
            Outcome = outcome,
            CashierId = cashier?.CashierId ?? session.CashierId,
            UserGuid = cashier?.UserGuid,
            CashierName = cashier?.CashierName ?? session.CashierName,
            IsOfflineCached = cashier?.IsOfflineCached == true,
            IsEmergencyOverride = cashier?.IsEmergencyOverride == true,
            StoreCode = session.StoreCode,
            DeviceCode = session.DeviceCode,
            CurrencyCode = "AUD",
            CorrelationId = correlationId,
            TraceId = traceId
        };

        var authorization = OperationAuthorizationScope.CurrentAuthorizationContext;
        if (!string.Equals(operationType, OperationAuditTypes.PermissionOverride, StringComparison.OrdinalIgnoreCase) &&
            authorization is not null)
        {
            // 中文注释：业务审计主体仍是请求收银员，只通过安全属性附加本次临时授权关联。
            auditEvent.Properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["requestingCashierId"] = cashier?.CashierId ?? session.CashierId,
                ["authorizingCashierId"] = authorization.AuthorizingSession.CashierId,
                ["authorizingUserGuid"] = authorization.AuthorizingSession.UserGuid,
                ["permissionCode"] = authorization.PermissionCode,
                ["authorizationMode"] = authorization.AuthorizingSession.IsOfflineCached ? "offline-cache" : "online",
                ["screen"] = authorization.Screen,
                ["action"] = authorization.Action
            };
        }

        return auditEvent;
    }

    private static void Record(IOperationAuditLogger logger, OperationAuditEventDto auditEvent)
    {
        try
        {
            // 操作日志永远是旁路能力，记录器异常不能反向打断销售、付款或退款。
            logger.Record(auditEvent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine($"[HBPOS][OperationAudit] record failed error={ex.GetType().Name}");
            Trace.WriteLine($"[HBPOS][OperationAudit] record failed error={ex.GetType().Name}");
        }
    }

    private static decimal RoundMoney(decimal value)
    {
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal RoundQuantity(decimal value)
    {
        return decimal.Round(value, 3, MidpointRounding.AwayFromZero);
    }
}
