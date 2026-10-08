using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface IOperationAuditIngestService
{
    Task<OperationAuditBatchResultDto> IngestAsync(
        OperationAuditBatchRequestDto request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken,
        string? deviceSystem = null);
}

public sealed class SqlSugarOperationAuditIngestService(
    HbposSqlSugarContext dbContext,
    TimeProvider? timeProvider = null) : IOperationAuditIngestService
{
    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Succeeded", "Denied", "Failed"
    };

    private static readonly HashSet<string> AllowedOperationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASHIER_LOGIN", "CASHIER_LOGOUT",
        "CART_ITEM_ADD", "CART_ITEM_REMOVE", "CART_ITEM_QUANTITY_CHANGE",
        "CART_ITEM_PRICE_CHANGE", "CART_LINE_DISCOUNT_CHANGE", "CART_ORDER_DISCOUNT_CHANGE", "CART_CLEAR",
        "ORDER_HOLD", "ORDER_RECALL", "ORDER_CANCEL",
        "CASH_DRAWER_OPEN", "PAYMENT_TENDER_ADD", "PAYMENT_TENDER_REMOVE", "PAYMENT_CANCEL", "SALE_COMPLETE",
        "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
        "RETURN_REFUND_COMPLETE", "SALE_VOID", "RECEIPT_REPRINT",
        "INSTALLMENT_REPAYMENT_COMPLETE", "INSTALLMENT_REPAYMENT_CANCEL",
        "DAILY_CLOSE_SAVE", "DAILY_CLOSE_REPRINT",
        "LINKLY_SETTLEMENT", "LINKLY_SETTLEMENT_REPRINT", "PERMISSION_OVERRIDE",
        "INSTALLMENT_PICKUP_CONFIRM", "INSTALLMENT_LINES_AMEND",
        // 收银端系统级操作：重置商品目录、重置测试销售数据、设备换绑、切换服务器地址、安装远程维护。
        "CATALOG_RESET", "TEST_SALES_DATA_RESET", "DEVICE_REREGISTER", "API_SERVER_CHANGE",
        "REMOTE_MAINTENANCE_INSTALL"
    };

    private static readonly HashSet<string> AllowedPropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "source", "action", "status", "screen", "mode", "reason", "result",
        "paymentMethod", "cashDrawerMode", "itemCount", "requestingCashierId",
        "authorizingCashierId", "authorizingUserGuid", "permissionCode", "authorizationMode",
        "attemptGuid", "operationGuid", "sessionId", "evidence", "financialReference"
    };

    private static readonly Regex UrlQueryRegex = new(
        @"(?<url>(?:https?://|/)[^\s?]+)\?[^\s]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BearerRegex = new(
        @"\bBearer\s+[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveAssignmentRegex = new(
        @"\b(authorization|authorizationCode|token|password|pin|secret|api[-_]?key|credential|pan|cvv|cardNumber|voucher[-_]?code|employee[-_]?barcode)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PanRegex = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<OperationAuditBatchResultDto> IngestAsync(
        OperationAuditBatchRequestDto request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken,
        string? deviceSystem = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new OperationAuditBatchResultDto();
        foreach (var auditEvent in request.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = Validate(auditEvent);
            if (validation is not null)
            {
                result.RejectedCount++;
                result.Results.Add(new OperationAuditItemResultDto
                {
                    EventId = auditEvent.EventId,
                    Status = "rejected",
                    ErrorCode = validation.Value.Code,
                    ErrorMessage = validation.Value.Message
                });
                continue;
            }

            var status = await PersistAsync(
                auditEvent,
                storeCode,
                deviceCode,
                deviceSystem,
                cancellationToken);
            if (status == "accepted")
            {
                result.AcceptedCount++;
            }
            else
            {
                result.DuplicateCount++;
            }

            result.Results.Add(new OperationAuditItemResultDto
            {
                EventId = auditEvent.EventId,
                Status = status
            });
        }

        return result;
    }

    private async Task<string> PersistAsync(
        OperationAuditEventDto auditEvent,
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            var exists = await db.Queryable<PosOperationAudit>()
                .AnyAsync(x => x.EventId == auditEvent.EventId, cancellationToken);
            if (exists)
            {
                await db.Ado.CommitTranAsync();
                return "duplicate";
            }

            var parent = MapParent(
                auditEvent,
                storeCode,
                deviceCode,
                deviceSystem,
                clock.GetUtcNow().UtcDateTime);
            var items = (auditEvent.Items ?? [])
                .Select((item, index) => MapItem(auditEvent.EventId, index, item))
                .ToList();

            await db.Insertable(parent).ExecuteCommandAsync(cancellationToken);
            if (items.Count > 0)
            {
                await db.Insertable(items).ExecuteCommandAsync(cancellationToken);
            }

            await db.Ado.CommitTranAsync();
            return "accepted";
        }
        catch
        {
            await db.Ado.RollbackTranAsync();

            // 并发重复写可能由主键竞争触发；只在数据库已经存在时降级为 duplicate，其余错误继续上抛以便客户端重试。
            if (await db.Queryable<PosOperationAudit>()
                    .AnyAsync(x => x.EventId == auditEvent.EventId, cancellationToken))
            {
                return "duplicate";
            }

            throw;
        }
    }

    private static (string Code, string Message)? Validate(OperationAuditEventDto auditEvent)
    {
        if (auditEvent.EventId == Guid.Empty)
        {
            return ("EVENT_ID_REQUIRED", "eventId is required");
        }

        if (auditEvent.SchemaVersion != 1)
        {
            return ("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion must be 1");
        }

        if (auditEvent.OccurredAtUtc == default)
        {
            return ("OCCURRED_AT_REQUIRED", "occurredAtUtc is required");
        }

        if (string.IsNullOrWhiteSpace(auditEvent.OperationType) ||
            !AllowedOperationTypes.Contains(auditEvent.OperationType.Trim()))
        {
            return ("INVALID_OPERATION_TYPE", "operationType is not supported");
        }

        if (string.IsNullOrWhiteSpace(auditEvent.Outcome) ||
            (!AllowedOutcomes.Contains(auditEvent.Outcome.Trim()) &&
             !ShouldNormalizeOutcomeToSucceeded(auditEvent.OperationType, auditEvent.Outcome)))
        {
            return ("INVALID_OUTCOME", "outcome must be Succeeded, Denied or Failed");
        }

        if (!string.IsNullOrWhiteSpace(auditEvent.CurrencyCode) &&
            !string.Equals(auditEvent.CurrencyCode.Trim(), "AUD", StringComparison.OrdinalIgnoreCase))
        {
            return ("INVALID_CURRENCY", "currencyCode must be AUD");
        }

        if (auditEvent.Items?.Any(static item => item is null) == true)
        {
            return ("INVALID_ITEM", "items cannot contain null");
        }

        return null;
    }

    private static PosOperationAudit MapParent(
        OperationAuditEventDto source,
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        DateTime receivedAtUtc)
    {
        var items = source.Items ?? [];
        return new PosOperationAudit
        {
            EventId = source.EventId,
            SchemaVersion = source.SchemaVersion,
            OccurredAtUtc = source.OccurredAtUtc.UtcDateTime,
            ReceivedAtUtc = DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc),
            OperationType = CanonicalOperationType(source.OperationType),
            Outcome = ShouldNormalizeOutcomeToSucceeded(source.OperationType, source.Outcome)
                ? "Succeeded"
                : CanonicalOutcome(source.Outcome),
            CashierId = CleanStructured(source.CashierId, 100),
            UserGuid = CleanStructured(source.UserGuid, 100),
            CashierName = CleanStructured(source.CashierName, 128),
            IsOfflineCached = source.IsOfflineCached,
            IsEmergencyOverride = source.IsEmergencyOverride,
            StoreCode = CleanRequiredStructured(storeCode, 50),
            DeviceCode = CleanRequiredStructured(deviceCode, 64),
            // 设备系统只由控制器从已认证设备 claim 传入，绝不相信审计请求体。
            DeviceSystem = CleanStructured(deviceSystem, 32),
            AppVersion = CleanStructured(source.AppVersion, 32),
            InstanceId = CleanStructured(source.InstanceId, 64),
            OrderGuid = CleanStructured(source.OrderGuid, 100),
            ReceiptNumber = CleanStructured(source.ReceiptNumber, 100),
            CorrelationId = CleanStructured(source.CorrelationId, 100),
            TraceId = CleanStructured(source.TraceId, 100),
            PaymentMethod = CleanStructured(source.PaymentMethod, 32),
            ReasonCode = CleanStructured(source.ReasonCode, 64),
            SafeMessage = CleanFreeText(source.SafeMessage, 1000),
            CurrencyCode = NormalizeCurrency(source.CurrencyCode),
            PaymentAmount = RoundMoney(source.PaymentAmount),
            BeforeGross = RoundMoney(source.BeforeGross),
            AfterGross = RoundMoney(source.AfterGross),
            BeforeDiscount = RoundMoney(source.BeforeDiscount),
            AfterDiscount = RoundMoney(source.AfterDiscount),
            BeforeActual = RoundMoney(source.BeforeActual),
            AfterActual = RoundMoney(source.AfterActual),
            AmountDelta = RoundMoney(source.AmountDelta),
            ProductCount = items.Count,
            PrimaryProduct = CleanStructured(items.FirstOrDefault()?.DisplayName ?? items.FirstOrDefault()?.ProductCode, 255),
            PropertiesJson = SerializeAllowedProperties(source.Properties)
        };
    }

    private static PosOperationAuditItem MapItem(Guid eventId, int index, OperationAuditItemDto source)
    {
        return new PosOperationAuditItem
        {
            EventId = eventId,
            LineIndex = index,
            ProductCode = CleanStructured(source.ProductCode, 100),
            ItemNumber = CleanStructured(source.ItemNumber, 100),
            ReferenceCode = CleanStructured(source.ReferenceCode, 100),
            LookupCode = CleanStructured(source.LookupCode, 100),
            DisplayName = CleanStructured(source.DisplayName, 255),
            LineKind = CleanStructured(source.LineKind, 32),
            BeforeQuantity = RoundQuantity(source.BeforeQuantity),
            AfterQuantity = RoundQuantity(source.AfterQuantity),
            QuantityDelta = RoundQuantity(source.QuantityDelta),
            BeforeUnitPrice = RoundMoney(source.BeforeUnitPrice),
            AfterUnitPrice = RoundMoney(source.AfterUnitPrice),
            UnitPriceDelta = RoundMoney(source.UnitPriceDelta),
            BeforeDiscountAmount = RoundMoney(source.BeforeDiscountAmount),
            AfterDiscountAmount = RoundMoney(source.AfterDiscountAmount),
            DiscountAmountDelta = RoundMoney(source.DiscountAmountDelta),
            BeforeGrossAmount = RoundMoney(source.BeforeGrossAmount),
            AfterGrossAmount = RoundMoney(source.AfterGrossAmount),
            GrossAmountDelta = RoundMoney(source.GrossAmountDelta),
            BeforeActualAmount = RoundMoney(source.BeforeActualAmount),
            AfterActualAmount = RoundMoney(source.AfterActualAmount),
            ActualAmountDelta = RoundMoney(source.ActualAmountDelta)
        };
    }

    private static string CanonicalOutcome(string value)
    {
        return AllowedOutcomes.First(x => string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string CanonicalOperationType(string value)
    {
        return AllowedOperationTypes.First(x =>
            string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLegacyCardPaymentSupervisorOutcome(string operationType, string outcome)
    {
        // 仅兼容已经写入本机 outbox 的旧三态 payload；新客户端统一发送 Succeeded。
        if (!string.Equals(
                operationType.Trim(),
                "CARD_PAYMENT_SUPERVISOR_RESOLUTION",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(outcome.Trim(), "ConfirmPaid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(outcome.Trim(), "ConfirmNotPaid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(outcome.Trim(), "ContinueWaiting", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldNormalizeOutcomeToSucceeded(string operationType, string outcome)
    {
        if (IsLegacyCardPaymentSupervisorOutcome(operationType, outcome))
        {
            return true;
        }

        // 兼容已经写入旧客户端 outbox 的主管结算结果；新客户端统一发送 Succeeded。
        return string.Equals(operationType.Trim(), "LINKLY_SETTLEMENT", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(outcome.Trim(), "SupervisorResolved", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCurrency(string? value)
    {
        _ = value;
        return "AUD";
    }

    private static decimal? RoundMoney(decimal? value) =>
        value is null ? null : decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);

    private static decimal? RoundQuantity(decimal? value) =>
        value is null ? null : decimal.Round(value.Value, 3, MidpointRounding.AwayFromZero);

    private static string CleanRequiredStructured(string value, int maxLength) =>
        CleanStructured(value, maxLength) ?? string.Empty;

    private static string? CleanStructured(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = UrlQueryRegex.Replace(value.Trim(), "${url}");
        sanitized = BearerRegex.Replace(sanitized, "Bearer [REDACTED]");
        sanitized = SensitiveAssignmentRegex.Replace(sanitized, "$1=[REDACTED]");
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static string? CleanFreeText(string? value, int maxLength)
    {
        var sanitized = CleanStructured(value, maxLength);
        if (sanitized is null)
        {
            return null;
        }

        sanitized = PanRegex.Replace(sanitized, "[REDACTED_CARD]");
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static string? SerializeAllowedProperties(IReadOnlyDictionary<string, string?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var safe = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in properties.Take(20))
        {
            if (!AllowedPropertyKeys.Contains(pair.Key))
            {
                continue;
            }

            safe[pair.Key] = CleanFreeText(pair.Value, 256);
            var json = JsonSerializer.Serialize(safe);
            if (json.Length <= 4000)
            {
                continue;
            }

            safe.Remove(pair.Key);
            break;
        }

        return safe.Count == 0 ? null : JsonSerializer.Serialize(safe);
    }
}
