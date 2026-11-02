using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IOrderHistoryService
{
    Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken);
}

public sealed class OrderHistoryService(IOrderHistoryRepository repository) : IOrderHistoryService
{
    public Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        return repository.QueryAsync(request, cancellationToken);
    }

    public Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        return repository.GetDetailsAsync(orderGuid, cancellationToken);
    }
}

public interface IOrderHistoryRepository
{
    Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, OrderHistoryDetailsDto>> GetDetailsByOrderGuidsAsync(
        IReadOnlyCollection<Guid> orderGuids,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarOrderHistoryRepository(
    HbposSqlSugarContext dbContext,
    IStoreTimeZoneResolver storeTimeZoneResolver) : IOrderHistoryRepository
{
    public async Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = request.StoreCode.Trim();

        // POSM 的 OrderTime 是门店本地墙钟时间，查询区间要先换算到门店时区再比较。
        var storeTimeZone = await storeTimeZoneResolver.ResolveAsync(storeCode, cancellationToken);
        var query = dbContext.PosmDb.Queryable<SalesOrder>()
            .Where(x => x.BranchCode == storeCode);

        if (!string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            var deviceCode = request.DeviceCode.Trim();
            query = query.Where(x => x.DeviceCode == deviceCode);
        }

        if (request.SoldFrom is not null)
        {
            var soldFrom = StoreWallClock.ToWallClock(request.SoldFrom.Value, storeTimeZone);
            query = query.Where(x => x.OrderTime >= soldFrom);
        }

        if (request.SoldTo is not null)
        {
            var soldTo = StoreWallClock.ToWallClock(request.SoldTo.Value, storeTimeZone);
            query = query.Where(x => x.OrderTime <= soldTo);
        }

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            query = await ApplyKeywordAsync(query, storeCode, request.Keyword, cancellationToken);
        }

        var take = Math.Clamp(request.Take, 1, 200);
        var skip = Math.Max(request.Skip, 0);
        // 翻页要求排序稳定：同一时间的订单用 OrderGuid 兜底，避免跨页重复或漏单。
        var orders = await query
            .OrderByDescending(x => x.OrderTime)
            .OrderBy(x => x.OrderGuid)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        var orderGuids = orders.Select(x => x.OrderGuid).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var payments = orderGuids.Count == 0
            ? new List<PaymentDetail>()
            : await dbContext.PosmDb.Queryable<PaymentDetail>()
                .Where(x => orderGuids.Contains(x.OrderGuid))
                .ToListAsync(cancellationToken);

        var paymentLabels = payments
            .Where(x => !string.IsNullOrWhiteSpace(x.OrderGuid))
            .GroupBy(x => x.OrderGuid)
            .ToDictionary(
                x => x.Key!,
                x => string.Join(", ", x.Select(payment => ((PaymentMethodKind)payment.PaymentMethod).ToString()).Distinct()));

        return new OrderHistoryQueryResponse(orders.Select(order => new OrderHistorySummaryDto(
            ParseGuid(order.OrderGuid),
            order.BranchCode ?? string.Empty,
            order.DeviceCode ?? string.Empty,
            order.CashierName ?? string.Empty,
            ToDateTimeOffset(order.OrderTime, storeTimeZone),
            Amount(order.TotalAmount),
            Amount(order.DiscountAmount),
            Amount(order.ActualAmount),
            Count(order.ItemCount),
            order.OrderGuid is not null && paymentLabels.TryGetValue(order.OrderGuid, out var paymentSummary) ? paymentSummary : string.Empty,
            FormatStatus(order.Status ?? 0))).ToList());
    }

    // 小票上的 8 位单号 = 订单 GUID 末 8 位（十六进制）。
    private const int OrderShortCodeLength = 8;

    // 订单号子串探针最多取多少个订单号。
    private const int OrderGuidProbeLimit = 200;

    // 按订单号搜索的关键字至少要有多少个十六进制字符。
    private const int MinOrderGuidFragmentLength = 4;

    // GUID 的文本长度（带连字符）与其中最长一段（最后一段）的十六进制位数。
    private const int MaxOrderGuidLength = 36;
    private const int MaxOrderGuidSegmentLength = 12;

    // 一个关键字经商品主档精确解析出的商品编码上限（正常 1~3 个）。
    private const int ResolvedProductCodeLimit = 50;

    // 这些商品编码在明细里总共（所有分店、全部时间）不超过这么多行，就当「冷门」：先取订单号再按订单号 IN 查，
    // 最多 1000 + OrderGuidProbeLimit 个参数，远低于 SQL Server 单语句 2100 个参数的上限。
    private const int ColdProductLineLimit = 1000;

    /// <summary>
    /// 关键字过滤。匹配规则：订单号包含该关键字，或订单里有商品的编码 = 关键字经商品主档精确解析出的编码。
    ///
    /// 为什么不再直接对明细的 Barcode / Remark 做文本匹配：生产实测（2026-11-02，3 百万行订单表 / 7 百万行明细表）
    /// 这两列没有索引，旧写法整表扫聚集索引再逐单探明细，平均 39 秒（约 140 万次逻辑读），而 WPF 查单超时只有 2 秒。
    /// 改成与主站收银记录页一致的做法：先用商品主档（Barcode / ItemNumber / ProductCode 都有索引）精确解析出商品编码，
    /// 再走 sales_order_detail.ProductCode 索引。
    ///
    /// 取舍（已由用户确认接受）：近 2 天 900 对「明细条码 + 商品编码」抽样里，约 4.4% 的明细条码在主档里已不存在
    /// （前导零写法、旧条码），约 0.2% 指向别的商品；这部分历史变体条码按条码搜不到订单。货号同理按主档当前货号解析，
    /// 不再解析明细备注里的 itemNo 文本。主档没有的 POS 临时商品仍可按完整商品编码搜到。
    /// </summary>
    private async Task<ISugarQueryable<SalesOrder>> ApplyKeywordAsync(
        ISugarQueryable<SalesOrder> query,
        string storeCode,
        string rawKeyword,
        CancellationToken cancellationToken)
    {
        var keyword = rawKeyword.Trim();

        // 第 1 步：订单号子串。GUID 里只有十六进制和连字符，别的关键字不可能命中，直接跳过这次扫描。
        var orderGuidMatches = IsOrderGuidFragment(keyword)
            ? await ProbeOrderGuidsAsync(storeCode, keyword, cancellationToken)
            : [];

        // 小票上的 8 位单号命中订单号就够了，不再去查商品：这是退货页最要紧的路径，要在 2 秒超时内返回。
        if (orderGuidMatches.Count > 0 && IsOrderShortCodeShape(keyword))
        {
            return query.Where(x => orderGuidMatches.Contains(x.OrderGuid!));
        }

        // 第 2 步：商品。关键字经主档精确解析成商品编码（再加上关键字本身，覆盖主档里没有的临时商品）。
        var productCodes = await ResolveKeywordProductCodesAsync(keyword, cancellationToken);

        // 第 3 步：带上限地探一下这些商品编码在明细里出现多少行（走 IX_sales_order_detail_ProductCode 覆盖索引，只读上限 + 1 行）。
        var productLineOrderGuids = await dbContext.PosmDb.Queryable<SalesOrderDetail>()
            .Where(line => productCodes.Contains(line.ProductCode!))
            .Select(line => line.OrderGuid)
            .Take(ColdProductLineLimit + 1)
            .ToListAsync(cancellationToken);

        if (productLineOrderGuids.Count <= ColdProductLineLimit)
        {
            // 冷门商品：订单号已经全拿到了，直接按订单号 IN 取行（主键点查），门店、终端、时间等过滤仍叠在 query 上。
            var matchedOrderGuids = orderGuidMatches
                .Concat(productLineOrderGuids.Where(orderGuid => !string.IsNullOrEmpty(orderGuid)).Select(orderGuid => orderGuid!))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return matchedOrderGuids.Count == 0
                ? query.Where("1 = 0")
                : query.Where(x => matchedOrderGuids.Contains(x.OrderGuid!));
        }

        // 热门商品：几乎每个订单都有，按订单号 IN 会超出参数上限；用 EXISTS 让数据库从本店最新的订单往回找，通常很快找满一页。
        // 在 C# 里决定是否带订单号条件，避免 SqlSugar 把布尔变量翻译成 SQL Server 的裸 bit 谓词。
        return orderGuidMatches.Count == 0
            ? query.Where(x => SqlFunc.Subqueryable<SalesOrderDetail>()
                .Where(line => line.OrderGuid == x.OrderGuid && productCodes.Contains(line.ProductCode!))
                .Any())
            : query.Where(x => orderGuidMatches.Contains(x.OrderGuid!)
                || SqlFunc.Subqueryable<SalesOrderDetail>()
                    .Where(line => line.OrderGuid == x.OrderGuid && productCodes.Contains(line.ProductCode!))
                    .Any());
    }

    private async Task<List<string>> ProbeOrderGuidsAsync(
        string storeCode,
        string keyword,
        CancellationToken cancellationToken)
    {
        var rows = await BuildOrderGuidProbeQuery(dbContext.PosmDb.Queryable<SalesOrder>(), storeCode, keyword)
            .ToListAsync(cancellationToken);
        return rows
            .Where(orderGuid => !string.IsNullOrEmpty(orderGuid))
            .Select(orderGuid => orderGuid!)
            .ToList();
    }

    /// <summary>
    /// 把关键字经商品主档（HBweb.Product，含已删除商品——历史订单里仍可能卖过）精确解析成商品编码：
    /// 编码、货号、条码三者任一等于关键字。精确相等，Barcode / ItemNumber / ProductCode 在主档上都有索引。
    /// </summary>
    private async Task<List<string>> ResolveKeywordProductCodesAsync(
        string keyword,
        CancellationToken cancellationToken)
    {
        var masterCodes = await dbContext.MainDb.Queryable<Product>()
            .Where(p => p.ProductCode != null
                && (p.ProductCode == keyword || p.ItemNumber == keyword || p.Barcode == keyword))
            .Select(p => p.ProductCode)
            .Take(ResolvedProductCodeLimit)
            .ToListAsync(cancellationToken);

        return masterCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Append(keyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool IsOrderShortCodeShape(string keyword)
    {
        if (keyword.Length != OrderShortCodeLength)
        {
            return false;
        }

        foreach (var ch in keyword)
        {
            // 只认 ASCII 十六进制；char.IsDigit 会放过全角数字等。
            if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 订单号（GUID）里只会有十六进制和连字符，且至少要有 MinOrderGuidFragmentLength 个十六进制字符才按订单号搜：
    /// 太短的片段（1~3 位）会命中成百上千个订单，探针只取任意 200 个，结果没有意义；纯连字符同理。
    /// 与主站收银记录页「≥4 位十六进制才按订单号匹配」的规则一致。
    /// </summary>
    internal static bool IsOrderGuidFragment(string keyword)
    {
        // 带连字符的完整 GUID 也只有 36 位。
        if (keyword.Length > MaxOrderGuidLength)
        {
            return false;
        }

        var hexDigits = 0;
        var hasHyphen = false;
        foreach (var ch in keyword)
        {
            if (ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                hexDigits++;
            }
            else if (ch == '-')
            {
                hasHyphen = true;
            }
            else
            {
                return false;
            }
        }

        // 库里的订单号带连字符，最长的一段只有 12 位十六进制：不含连字符的片段超过 12 位不可能命中，
        // 典型的就是 13 位 EAN 商品条码——跳过这次约 0.5 秒的订单号扫描。
        if (!hasHyphen && hexDigits > MaxOrderGuidSegmentLength)
        {
            return false;
        }

        return hexDigits >= MinOrderGuidFragmentLength;
    }

    /// <summary>
    /// 只查订单号（最多 OrderGuidProbeLimit 个）：SELECT 列表里不能出现 Remark、CashierName 等不在覆盖索引里的列，
    /// 否则优化器会放弃 IX_sales_order_BranchCode 转而整表扫聚集索引；也不带明细子查询。
    /// 这里刻意不写 ORDER BY：生产实测加上 ORDER BY OrderTime DESC 后，优化器为了省掉一次排序，
    /// 改去扫覆盖全部分店的 IX_sales_order_OrderTime（约 300 万行 / 5.5 万页，1.2~2.2 秒），
    /// 不排序时只扫本店的 IX_sales_order_BranchCode（约 1.9 千~4 千页，不到 0.7 秒）。
    /// 排序留给后面按精确订单号取行的那条查询。库里的订单号全是小写，关键字转小写后比较，不依赖库排序规则。
    /// </summary>
    internal static ISugarQueryable<string?> BuildOrderGuidProbeQuery(
        ISugarQueryable<SalesOrder> source,
        string storeCode,
        string keyword)
    {
        var lowered = keyword.Trim().ToLowerInvariant();
        var normalized = lowered.Replace("-", string.Empty, StringComparison.Ordinal);
        var scoped = source.Where(x => x.BranchCode == storeCode && x.OrderGuid != null);
        // 在 C# 中决定是否带「去连字符」的第二个条件，避免布尔变量被翻译成 SQL Server 的裸 bit 谓词。
        var matched = normalized == lowered
            ? scoped.Where(x => SqlFunc.CharIndexNew(x.OrderGuid!, lowered) > 0)
            : scoped.Where(x => SqlFunc.CharIndexNew(x.OrderGuid!, lowered) > 0
                || SqlFunc.CharIndexNew(x.OrderGuid!, normalized) > 0);
        return matched
            .Select(x => x.OrderGuid)
            .Take(OrderGuidProbeLimit);
    }

    public async Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var orders = await GetDetailsByOrderGuidsAsync([orderGuid], cancellationToken);
        return orders.GetValueOrDefault(orderGuid);
    }

    public async Task<IReadOnlyDictionary<Guid, OrderHistoryDetailsDto>> GetDetailsByOrderGuidsAsync(
        IReadOnlyCollection<Guid> orderGuids,
        CancellationToken cancellationToken)
    {
        var orderGuidTexts = orderGuids
            .Where(orderGuid => orderGuid != Guid.Empty)
            .Distinct()
            .Select(orderGuid => orderGuid.ToString("D"))
            .ToList();
        if (orderGuidTexts.Count == 0)
        {
            return new Dictionary<Guid, OrderHistoryDetailsDto>();
        }

        var orders = await dbContext.PosmDb.Queryable<SalesOrder>()
            .Where(order => order.OrderGuid != null && orderGuidTexts.Contains(order.OrderGuid))
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return new Dictionary<Guid, OrderHistoryDetailsDto>();
        }

        var foundOrderGuidTexts = orders
            .Select(order => order.OrderGuid)
            .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
            .Select(orderGuid => orderGuid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = await dbContext.PosmDb.Queryable<SalesOrderDetail>()
            .Where(line => foundOrderGuidTexts.Contains(line.OrderGuid))
            .ToListAsync(cancellationToken);
        var payments = await dbContext.PosmDb.Queryable<PaymentDetail>()
            .Where(payment => payment.OrderGuid != null && foundOrderGuidTexts.Contains(payment.OrderGuid))
            .ToListAsync(cancellationToken);
        var bankTransactions = await dbContext.PosmDb.Queryable<BankTransaction>()
            .Where(transaction => transaction.OrderGuid != null && foundOrderGuidTexts.Contains(transaction.OrderGuid))
            .ToListAsync(cancellationToken);

        var linesByOrder = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.OrderGuid))
            .GroupBy(line => line.OrderGuid!)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);
        var paymentsByOrder = payments
            .Where(payment => !string.IsNullOrWhiteSpace(payment.OrderGuid))
            .GroupBy(payment => payment.OrderGuid!)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);
        var bankTransactionsByOrder = bankTransactions
            .Where(transaction => !string.IsNullOrWhiteSpace(transaction.OrderGuid))
            .GroupBy(transaction => transaction.OrderGuid!)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);
        // 一次可能查多家门店的订单，时间要按各自门店的时区还原。
        var storeTimeZones = new Dictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var branchCode in orders
            .Select(order => order.BranchCode?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            storeTimeZones[branchCode] = await storeTimeZoneResolver.ResolveAsync(branchCode, cancellationToken);
        }

        var result = new Dictionary<Guid, OrderHistoryDetailsDto>();
        foreach (var order in orders)
        {
            if (!Guid.TryParse(order.OrderGuid, out var orderGuid))
            {
                continue;
            }

            var orderGuidText = order.OrderGuid!;
            result[orderGuid] = MapDetails(
                orderGuid,
                order,
                linesByOrder.GetValueOrDefault(orderGuidText) ?? [],
                paymentsByOrder.GetValueOrDefault(orderGuidText) ?? [],
                bankTransactionsByOrder.GetValueOrDefault(orderGuidText) ?? [],
                storeTimeZones[order.BranchCode?.Trim() ?? string.Empty]);
        }

        return result;
    }

    private static OrderHistoryDetailsDto MapDetails(
        Guid orderGuid,
        SalesOrder order,
        IReadOnlyList<SalesOrderDetail> lines,
        IReadOnlyList<PaymentDetail> payments,
        IReadOnlyList<BankTransaction> bankTransactions,
        TimeZoneInfo storeTimeZone)
    {
        var bankTransactionsByPayment = bankTransactions
            .Where(transaction => !string.IsNullOrWhiteSpace(transaction.PaymentGuid))
            .GroupBy(transaction => transaction.PaymentGuid!)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new OrderHistoryDetailsDto(
            orderGuid,
            order.BranchCode ?? string.Empty,
            order.DeviceCode ?? string.Empty,
            order.CashierName ?? string.Empty,
            ToDateTimeOffset(order.OrderTime, storeTimeZone),
            Amount(order.TotalAmount),
            Amount(order.DiscountAmount),
            Amount(order.ActualAmount),
            lines.Select(line => new OrderHistoryLineDto(
                ParseGuid(line.OrderDetailGuid),
                line.ProductCode ?? string.Empty,
                line.ReferenceGUID,
                line.ProductName ?? string.Empty,
                line.Barcode ?? string.Empty,
                ExtractItemNo(line.Remark),
                Count(line.Quantity),
                Amount(line.Price),
                Amount(line.DiscountAmount),
                Amount(line.ActualAmount),
                OrderLineKind.Sale,
                null,
                null,
                null)).ToList(),
            payments.Select(payment => new OrderHistoryPaymentDto(
                ParseGuid(payment.PaymentGuid),
                (PaymentMethodKind)payment.PaymentMethod,
                Amount(payment.Amount),
                payment.Reference,
                payment.PaymentGuid is not null && bankTransactionsByPayment.TryGetValue(payment.PaymentGuid, out var cardTransactions)
                    ? cardTransactions.Select(transaction => ToCardTransactionDto(payment.Reference, transaction, storeTimeZone)).ToList()
                    : null)).ToList());
    }

    private static CardTransactionDto ToCardTransactionDto(
        string? paymentReference,
        BankTransaction transaction,
        TimeZoneInfo storeTimeZone)
    {
        return new CardTransactionDto(
            InferCardProcessor(paymentReference),
            transaction.TxnRef,
            transaction.AuthCode,
            transaction.CardType,
            transaction.CardBIN,
            transaction.CardNumber,
            transaction.Caid,
            transaction.ResponseCode,
            transaction.ResponseText,
            transaction.Stan,
            transaction.BankDateTime is null
                ? null
                : StoreWallClock.ToDateTimeOffset(transaction.BankDateTime.Value, storeTimeZone),
            Amount(transaction.Amount),
            transaction.ReceiptText,
            TryGetLinklyRefundReference(paymentReference));
    }

    private static string? TryGetLinklyRefundReference(string? paymentReference)
    {
        var backendRefundReference = LinklyBackendPaymentReference.TryGetRefundReference(paymentReference);
        if (!string.IsNullOrWhiteSpace(backendRefundReference))
        {
            return backendRefundReference;
        }

        var parts = paymentReference?.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        return parts.Length >= 3 &&
            string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase)
                ? parts[2]
                : null;
    }

    private static string InferCardProcessor(string? paymentReference)
    {
        var displayReference = CardRefundReference.GetDisplayReference(paymentReference);
        if (ManualCardPaymentReference.IsManual(displayReference))
        {
            return ManualCardPaymentReference.Processor;
        }

        if (displayReference?.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "ANZ";
        }

        return displayReference?.StartsWith("SQ", StringComparison.OrdinalIgnoreCase) == true
            ? "Square"
            : "Card";
    }

    private static Guid ParseGuid(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : Guid.Empty;
    }

    private static string? ExtractItemNo(string? remark)
    {
        if (string.IsNullOrWhiteSpace(remark))
        {
            return null;
        }

        const string marker = "itemNo=";
        var index = remark.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = remark.IndexOf(';', start);
        return end < 0 ? remark[start..].Trim() : remark[start..end].Trim();
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime? value, TimeZoneInfo storeTimeZone)
    {
        return value is null
            ? new DateTimeOffset(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc))
            : StoreWallClock.ToDateTimeOffset(value.Value, storeTimeZone);
    }

    private static decimal Amount(decimal? value)
    {
        return value ?? 0m;
    }

    private static int Count(int? value)
    {
        return value ?? 0;
    }

    private static string FormatStatus(int status)
    {
        return status switch
        {
            1 => "Completed",
            2 => "Voided",
            _ => status.ToString()
        };
    }
}
