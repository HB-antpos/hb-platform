using System.Text.Json;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Api.Services;

public interface IDailyCloseSyncService
{
    Task<DailyCloseSyncResponse> SyncAsync(
        DailyCloseSyncRequest request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// 日结记录上传：校验并规整客户端快照，再按 DailyCloseGuid 做幂等入库。
/// 上传范围只信任调用方传入的认证 scope（storeCode / deviceCode），请求体里的同名字段仅用于一致性校验（控制器已做）。
/// </summary>
internal sealed class DailyCloseSyncService(
    IDailyCloseRepository repository,
    TimeProvider? timeProvider = null) : IDailyCloseSyncService
{
    private const int MaximumSyncAttempts = 5;
    private const int MaximumCashierIdLength = 64;
    private const int MaximumCashierNameLength = 128;
    private const int MaximumAppVersionLength = 64;
    private const int MaximumCashCountQuantity = 100_000;
    private const decimal MaximumAbsoluteValue = 100_000_000m;

    // Net 与 Sales−Refund、CashDifference 与 Counted−CashNet 允许相差 1 分，吸收客户端各自四舍五入的误差。
    private const decimal RoundingTolerance = 0.01m;

    private static readonly JsonSerializerOptions CashCountsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DailyCloseSyncResponse> SyncAsync(
        DailyCloseSyncRequest request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = ValidateAndNormalize(request, storeCode, deviceCode, clock.GetUtcNow().UtcDateTime);

        // 重试循环处理三类竞态：并发首次插入撞唯一键、并发覆盖同一占位、占位在读与写之间被别的请求补全。
        for (var attempt = 0; attempt < MaximumSyncAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await repository.GetByGuidAsync(normalized.DailyCloseGuid, cancellationToken);
            if (existing is null)
            {
                if (await repository.TryInsertAsync(normalized, cancellationToken))
                {
                    return new DailyCloseSyncResponse(Accepted: true, AlreadySynced: false, ReplacedPlaceholder: false);
                }

                // 唯一键冲突：别的请求刚插入了同一个 Guid，重读后按已存在处理。
                continue;
            }

            // 先查归属：Guid 已被其它门店/设备占用时一律冲突，绝不能让一个设备覆盖或探测另一个设备的记录。
            if (!SameCode(existing.StoreCode, normalized.StoreCode) ||
                !SameCode(existing.DeviceCode, normalized.DeviceCode))
            {
                throw Conflict(
                    "DAILY_CLOSE_SCOPE_CONFLICT",
                    "The daily close record already belongs to another store or device.");
            }

            if (!string.Equals(existing.DetailLevel, DailyCloseDetailLevels.Full, StringComparison.Ordinal))
            {
                // 回填占位：用客户端完整数据覆盖；写入带 DetailLevel <> 'Full' 守卫，失败说明被并发补全，重读再判断。
                normalized.Id = existing.Id;
                if (await repository.TryReplacePlaceholderAsync(normalized, cancellationToken))
                {
                    return new DailyCloseSyncResponse(Accepted: true, AlreadySynced: false, ReplacedPlaceholder: true);
                }

                continue;
            }

            if (Equivalent(existing, normalized))
            {
                return new DailyCloseSyncResponse(Accepted: true, AlreadySynced: true, ReplacedPlaceholder: false);
            }

            throw Conflict(
                "DAILY_CLOSE_CONTENT_CONFLICT",
                "The same daily close was uploaded earlier with different content.");
        }

        throw Conflict(
            "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE",
            "The daily close record changed concurrently. Retry the upload.");
    }

    private static PosmDailyCloseRecord ValidateAndNormalize(
        DailyCloseSyncRequest request,
        string storeCode,
        string deviceCode,
        DateTime nowUtc)
    {
        if (request.SchemaVersion != DailyCloseContractConstants.SchemaVersion)
        {
            throw Invalid("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion must be 1.");
        }

        if (request.DailyCloseGuid == Guid.Empty)
        {
            throw Invalid("DAILY_CLOSE_GUID_REQUIRED", "dailyCloseGuid is required.");
        }

        var normalizedStoreCode = Required(storeCode, 32, "STORE_CODE_REQUIRED", "Authenticated store code is required.");
        var normalizedDeviceCode = Required(deviceCode, 64, "DEVICE_CODE_REQUIRED", "Authenticated device code is required.");
        var clientKind = Canonical(
            request.ClientKind,
            DailyCloseContractConstants.ClientKinds,
            "INVALID_CLIENT_KIND",
            "clientKind must be Wpf, Handheld or Ipad.");

        if (request.BusinessDate == default)
        {
            throw Invalid("BUSINESS_DATE_REQUIRED", "businessDate is required.");
        }

        if (request.PeriodFrom == default || request.PeriodTo == default)
        {
            throw Invalid("PERIOD_REQUIRED", "periodFrom and periodTo are required.");
        }

        if (request.SavedAt == default)
        {
            throw Invalid("SAVED_AT_REQUIRED", "savedAt is required.");
        }

        // 营业日以门店本地时区为准，可能比保存时刻的 UTC 日期晚一天（澳洲东部早间保存时 UTC 仍是前一天）；
        // 再往后就是错误数据，不能入库污染看板。
        var savedUtcDate = DateOnly.FromDateTime(request.SavedAt.UtcDateTime);
        var latestBusinessDate = savedUtcDate == DateOnly.MaxValue ? savedUtcDate : savedUtcDate.AddDays(1);
        if (request.BusinessDate > latestBusinessDate)
        {
            throw Invalid("INVALID_BUSINESS_DATE", "businessDate cannot be later than the saved date.");
        }

        // 收银员为空（例如后台补传、会话已过期）是允许的，null 与空串等价，统一存空串，列是 NOT NULL。
        var cashierId = Optional(request.CashierId, MaximumCashierIdLength, "CASHIER_ID_TOO_LONG") ?? string.Empty;
        var cashierName = Optional(request.CashierName, MaximumCashierNameLength, "CASHIER_NAME_TOO_LONG") ?? string.Empty;
        var appVersion = Optional(request.AppVersion, MaximumAppVersionLength, "APP_VERSION_TOO_LONG");

        if (request.OrderCount < 0)
        {
            throw Invalid("INVALID_ORDER_COUNT", "orderCount cannot be negative.");
        }

        RequireWithinLimit(request.ReturnQuantity, "INVALID_RETURN_QUANTITY", "returnQuantity");
        RequireWithinLimit(request.RefundAmount, "AMOUNT_OUT_OF_RANGE", "refundAmount");

        var tenders = ValidateTenders(request.Tenders);
        var cash = tenders[DailyCloseContractConstants.TenderCash];
        var card = tenders[DailyCloseContractConstants.TenderCard];
        var voucher = tenders[DailyCloseContractConstants.TenderVoucher];

        var (cashCountsJson, noteCents, coinCents) = ValidateCashCounts(request.CashCounts);
        RequireWithinLimit(request.NoteSubtotal, "AMOUNT_OUT_OF_RANGE", "noteSubtotal");
        RequireWithinLimit(request.CoinSubtotal, "AMOUNT_OUT_OF_RANGE", "coinSubtotal");
        RequireWithinLimit(request.CountedCashAmount, "AMOUNT_OUT_OF_RANGE", "countedCashAmount");
        RequireWithinLimit(request.CashDifference, "AMOUNT_OUT_OF_RANGE", "cashDifference");

        var noteSubtotal = RoundMoney(request.NoteSubtotal);
        var coinSubtotal = RoundMoney(request.CoinSubtotal);
        var countedCashAmount = RoundMoney(request.CountedCashAmount);
        var cashDifference = RoundMoney(request.CashDifference);

        // 盘点金额只能由数量推出，客户端汇总值必须与之精确一致。
        // 纸币 + 硬币 = 实点现金由这三个等式共同保证，无需再单独校验一次。
        if (noteSubtotal != noteCents / 100m)
        {
            throw Invalid("INVALID_NOTE_SUBTOTAL", "noteSubtotal does not match the cash counts.");
        }

        if (coinSubtotal != coinCents / 100m)
        {
            throw Invalid("INVALID_COIN_SUBTOTAL", "coinSubtotal does not match the cash counts.");
        }

        if (countedCashAmount != (noteCents + coinCents) / 100m)
        {
            throw Invalid("INVALID_COUNTED_CASH_AMOUNT", "countedCashAmount does not match the cash counts.");
        }

        // 差额 = 实点 − 应有现金（现金净额）；正为长款、负为短款。
        if (Math.Abs(cashDifference - (countedCashAmount - cash.NetAmount)) > RoundingTolerance)
        {
            throw Invalid("INVALID_CASH_DIFFERENCE", "cashDifference must equal countedCashAmount minus the cash net amount.");
        }

        return new PosmDailyCloseRecord
        {
            DailyCloseGuid = request.DailyCloseGuid,
            StoreCode = normalizedStoreCode,
            DeviceCode = normalizedDeviceCode,
            ClientKind = clientKind,
            DetailLevel = DailyCloseDetailLevels.Full,
            DataSource = DailyCloseDataSources.ClientUpload,
            BackfillBatch = null,
            BusinessDate = request.BusinessDate.ToDateTime(TimeOnly.MinValue),
            BusinessDateInferred = false,
            PeriodFromUtc = request.PeriodFrom.UtcDateTime,
            PeriodToUtc = request.PeriodTo.UtcDateTime,
            CashierId = cashierId,
            CashierName = cashierName,
            SavedAtUtc = request.SavedAt.UtcDateTime,
            AppVersion = appVersion,
            OrderCount = request.OrderCount,
            ReturnQuantity = decimal.Round(request.ReturnQuantity, 3, MidpointRounding.AwayFromZero),
            CashSalesAmount = cash.SalesAmount,
            CashRefundAmount = cash.RefundAmount,
            CashNetAmount = cash.NetAmount,
            CardSalesAmount = card.SalesAmount,
            CardRefundAmount = card.RefundAmount,
            CardNetAmount = card.NetAmount,
            VoucherSalesAmount = voucher.SalesAmount,
            VoucherRefundAmount = voucher.RefundAmount,
            VoucherNetAmount = voucher.NetAmount,
            RefundAmount = RoundMoney(request.RefundAmount),
            ExpectedCashAmount = cash.NetAmount,
            CountedCashAmount = countedCashAmount,
            CashDifference = cashDifference,
            NoteSubtotal = noteSubtotal,
            CoinSubtotal = coinSubtotal,
            CashCountsJson = cashCountsJson,
            ReceivedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    /// <summary>支付方式必须恰好是 Cash / Card / Voucher 各一条；金额取整后 Net 需与 Sales−Refund 吻合。</summary>
    private static Dictionary<string, NormalizedTender> ValidateTenders(IReadOnlyList<DailyCloseTenderSync>? tenders)
    {
        if (tenders is null || tenders.Count != DailyCloseContractConstants.TenderMethods.Count)
        {
            throw Invalid("INVALID_TENDERS", "tenders must contain exactly one Cash, Card and Voucher entry.");
        }

        var result = new Dictionary<string, NormalizedTender>(StringComparer.Ordinal);
        foreach (var tender in tenders)
        {
            if (tender is null)
            {
                throw Invalid("INVALID_TENDERS", "tenders cannot contain null entries.");
            }

            var method = Canonical(
                tender.Method,
                DailyCloseContractConstants.TenderMethods,
                "INVALID_TENDERS",
                "tender method must be Cash, Card or Voucher.");
            if (result.ContainsKey(method))
            {
                throw Invalid("INVALID_TENDERS", "tender methods cannot repeat.");
            }

            RequireWithinLimit(tender.SalesAmount, "AMOUNT_OUT_OF_RANGE", "tender salesAmount");
            RequireWithinLimit(tender.RefundAmount, "AMOUNT_OUT_OF_RANGE", "tender refundAmount");
            RequireWithinLimit(tender.NetAmount, "AMOUNT_OUT_OF_RANGE", "tender netAmount");
            var sales = RoundMoney(tender.SalesAmount);
            var refund = RoundMoney(tender.RefundAmount);
            var net = RoundMoney(tender.NetAmount);
            if (Math.Abs(net - (sales - refund)) > RoundingTolerance)
            {
                throw Invalid("TENDER_NET_MISMATCH", "tender netAmount must equal salesAmount minus refundAmount.");
            }

            result[method] = new NormalizedTender(sales, refund, net);
        }

        return result;
    }

    /// <summary>
    /// 现金盘点必须恰好覆盖 11 档面额各一次。返回按面额降序序列化的 JSON，以及纸币 / 硬币各自的总分值（分）。
    /// </summary>
    private static (string Json, long NoteCents, long CoinCents) ValidateCashCounts(
        IReadOnlyList<DailyCloseCashCountSync>? cashCounts)
    {
        var denominations = DailyCloseContractConstants.DenominationCents;
        if (cashCounts is null || cashCounts.Count != denominations.Count)
        {
            throw Invalid("INVALID_CASH_COUNTS", "cashCounts must contain exactly one entry for each of the 11 denominations.");
        }

        var quantities = new Dictionary<int, int>();
        foreach (var count in cashCounts)
        {
            if (count is null || !denominations.Contains(count.DenominationCents))
            {
                throw Invalid("INVALID_CASH_COUNTS", "cashCounts contains an unsupported denomination.");
            }

            if (!quantities.TryAdd(count.DenominationCents, count.Quantity))
            {
                throw Invalid("INVALID_CASH_COUNTS", "cashCounts cannot repeat a denomination.");
            }

            if (count.Quantity < 0 || count.Quantity > MaximumCashCountQuantity)
            {
                throw Invalid(
                    "INVALID_CASH_COUNT_QUANTITY",
                    $"cash count quantity must be between 0 and {MaximumCashCountQuantity}.");
            }
        }

        long noteCents = 0;
        long coinCents = 0;
        var ordered = new List<CashCountJson>(denominations.Count);
        foreach (var denomination in denominations)
        {
            var quantity = quantities[denomination];
            ordered.Add(new CashCountJson(denomination, quantity));
            var cents = (long)denomination * quantity;
            if (denomination >= DailyCloseContractConstants.NoteMinimumDenominationCents)
            {
                noteCents += cents;
            }
            else
            {
                coinCents += cents;
            }
        }

        return (JsonSerializer.Serialize(ordered, CashCountsJsonOptions), noteCents, coinCents);
    }

    /// <summary>已入库的 Full 记录与本次上传在全部上传字段上是否等价（金额已按 2 位小数规整，时间按 100ns 刻度比较）。</summary>
    private static bool Equivalent(PosmDailyCloseRecord existing, PosmDailyCloseRecord incoming)
    {
        return existing.DailyCloseGuid == incoming.DailyCloseGuid &&
            SameCode(existing.StoreCode, incoming.StoreCode) &&
            SameCode(existing.DeviceCode, incoming.DeviceCode) &&
            SameCode(existing.ClientKind, incoming.ClientKind) &&
            existing.BusinessDate.Date == incoming.BusinessDate.Date &&
            SameInstant(existing.PeriodFromUtc, incoming.PeriodFromUtc) &&
            SameInstant(existing.PeriodToUtc, incoming.PeriodToUtc) &&
            SameInstant(existing.SavedAtUtc, incoming.SavedAtUtc) &&
            string.Equals(existing.CashierId, incoming.CashierId, StringComparison.Ordinal) &&
            string.Equals(existing.CashierName, incoming.CashierName, StringComparison.Ordinal) &&
            // 刻意不比较 AppVersion：它只是上传方的客户端版本元数据，不是日结的业务内容。
            // 首次上传已被收下、响应丢失后客户端升级再重发时版本号会变，这不能被判成内容冲突，
            // 否则客户端会把一条服务端早已持有的记录标成永久拒绝。已存数据保持首次上传时的版本。
            existing.OrderCount == incoming.OrderCount &&
            existing.ReturnQuantity == incoming.ReturnQuantity &&
            existing.CashSalesAmount == incoming.CashSalesAmount &&
            existing.CashRefundAmount == incoming.CashRefundAmount &&
            existing.CashNetAmount == incoming.CashNetAmount &&
            existing.CardSalesAmount == incoming.CardSalesAmount &&
            existing.CardRefundAmount == incoming.CardRefundAmount &&
            existing.CardNetAmount == incoming.CardNetAmount &&
            existing.VoucherSalesAmount == incoming.VoucherSalesAmount &&
            existing.VoucherRefundAmount == incoming.VoucherRefundAmount &&
            existing.VoucherNetAmount == incoming.VoucherNetAmount &&
            existing.RefundAmount == incoming.RefundAmount &&
            existing.ExpectedCashAmount == incoming.ExpectedCashAmount &&
            existing.CountedCashAmount == incoming.CountedCashAmount &&
            existing.CashDifference == incoming.CashDifference &&
            existing.NoteSubtotal == incoming.NoteSubtotal &&
            existing.CoinSubtotal == incoming.CoinSubtotal &&
            string.Equals(existing.CashCountsJson, incoming.CashCountsJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// SQL Server 返回的 DATETIME2 是 Kind=Unspecified 的 DateTime，而上传侧是 UTC；
    /// 两者都表示 UTC 时刻，所以只比较 Ticks，避免 Kind 不同造成误判。
    /// </summary>
    private static bool SameInstant(DateTime? left, DateTime? right)
    {
        return left?.Ticks == right?.Ticks;
    }

    /// <summary>门店 / 设备编码按不区分大小写比较，与库的默认排序规则一致，避免大小写差异造成误冲突。</summary>
    private static bool SameCode(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static decimal RoundMoney(decimal value)
    {
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static void RequireWithinLimit(decimal value, string errorCode, string field)
    {
        if (Math.Abs(value) > MaximumAbsoluteValue)
        {
            throw Invalid(errorCode, $"{field} is out of range.");
        }
    }

    private static string Canonical(
        string? value,
        IEnumerable<string> allowed,
        string errorCode,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(errorCode, message);
        }

        var canonical = allowed.FirstOrDefault(candidate =>
            string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return canonical ?? throw Invalid(errorCode, message);
    }

    private static string Required(string? value, int maxLength, string errorCode, string message)
    {
        return Optional(value, maxLength, errorCode) ?? throw Invalid(errorCode, message);
    }

    private static string? Optional(string? value, int maxLength, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw Invalid(errorCode, $"Value exceeds {maxLength} characters.");
    }

    private static DailyCloseValidationException Invalid(string code, string message)
    {
        return new DailyCloseValidationException(code, message);
    }

    private static DailyCloseConflictException Conflict(string code, string message)
    {
        return new DailyCloseConflictException(code, message);
    }

    private readonly record struct NormalizedTender(decimal SalesAmount, decimal RefundAmount, decimal NetAmount);

    private sealed record CashCountJson(int DenominationCents, int Quantity);
}

public sealed class DailyCloseValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class DailyCloseConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
