using System.Globalization;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using C = BlazorApp.Api.Services.OperationAudits.Risk.PosOperationAuditRiskCatalog;

namespace BlazorApp.Api.Services.OperationAudits.Risk;

/// <summary>折扣 / 改价事件的首个商品行；判定幅度要用行级单价与折扣额。</summary>
public sealed record PosAuditRiskItem(
    string? DisplayName,
    decimal? Quantity,
    decimal? BeforeUnitPrice,
    decimal? AfterUnitPrice,
    decimal? BeforeDiscountAmount,
    decimal? AfterDiscountAmount,
    decimal? AfterGrossAmount
);

/// <summary>扫描读出的一条审计事件。OccurredAtUtc 用于排序与窗口，LocalTime 是门店墙钟，用于营业时间与依据文案。</summary>
public sealed record PosAuditRiskRow(
    Guid EventId,
    string StoreCode,
    string? DeviceCode,
    string? CashierId,
    string? CashierName,
    string OperationType,
    string Outcome,
    string? ReasonCode,
    string? OrderGuid,
    DateTime OccurredAtUtc,
    DateTime LocalTime,
    decimal? BeforeActual = null,
    decimal? AfterActual = null,
    decimal? BeforeDiscount = null,
    decimal? AfterDiscount = null,
    decimal? PaymentAmount = null,
    string? PaymentMethod = null,
    bool IsEmergencyOverride = false,
    string? PrimaryProduct = null,
    int ProductCount = 0,
    PosAuditRiskItem? Item = null
);

/// <summary>
/// 一次判定的输入：单店、评估窗口 [EvalFromUtc, EvalToUtc)。Rows 须覆盖规则所需的上下文：
/// 操作序列在窗口两侧各留 <see cref="PosOperationAuditRiskEvaluator.SequenceMargin"/>；
/// 重打印 / 销售与非营业时间的行从窗口起点所在的门店本地日零点读起。只有落在窗口内的事件会被标记。
/// </summary>
public sealed record PosAuditRiskInput(
    string StoreCode,
    DateTime EvalFromUtc,
    DateTime EvalToUtc,
    IReadOnlyList<PosAuditRiskRow> Rows
);

public sealed record PosAuditRiskFlag(PosAuditRiskRow Target, string RuleCode, IReadOnlyDictionary<string, string> Evidence);

/// <summary>
/// 新收银七条异常规则的纯函数判定，口径与老收银对齐（规则编号、依据键名相同，前端共用文案）：
/// 标记目标都选「稳定」的那一条（连续删除第 N 次、同一张小票第 N 次重打印、非营业时段第一条），
/// 审计晚到或重复扫描时目标不漂移，核查结论才挂得住。依据里的时刻是门店墙钟 HH:mm:ss。
/// </summary>
public static class PosOperationAuditRiskEvaluator
{
    /// <summary>操作序列在评估窗口两侧需要的余量：覆盖开钱箱 / 收款后删除窗口，以及连续删除要回看两倍窗口。</summary>
    public static TimeSpan SequenceMargin(LegacyEmployeeLogRiskOptions options) =>
        LegacyEmployeeLogRiskEvaluator.SequenceMargin(options);

    public static IReadOnlyList<PosAuditRiskFlag> Evaluate(PosAuditRiskInput input, LegacyEmployeeLogRiskOptions options)
    {
        var rows = input.Rows
            .Where(row => row.StoreCode == input.StoreCode)
            .DistinctBy(row => row.EventId)
            .OrderBy(row => row.OccurredAtUtc)
            .ThenBy(row => row.EventId)
            .ToList();
        bool InWindow(PosAuditRiskRow row) => row.OccurredAtUtc >= input.EvalFromUtc && row.OccurredAtUtc < input.EvalToUtc;

        var flags = new List<PosAuditRiskFlag>();
        foreach (var deviceRows in rows.GroupBy(row => row.DeviceCode ?? string.Empty).Select(group => group.ToList()))
        {
            flags.AddRange(NoSaleDrawer(deviceRows, options).Where(flag => InWindow(flag.Target)));
            flags.AddRange(DeleteAfterCheckout(deviceRows, options).Where(flag => InWindow(flag.Target)));
            flags.AddRange(RepeatReprint(deviceRows, options).Where(flag => InWindow(flag.Target)));
            flags.AddRange(OffHours(deviceRows, options).Where(flag => InWindow(flag.Target)));
        }
        flags.AddRange(BigDiscount(rows.Where(InWindow), options));
        flags.AddRange(BurstDelete(rows, options).Where(flag => InWindow(flag.Target)));
        flags.AddRange(EmergencyOverride(rows.Where(InWindow)));
        return flags;
    }

    /// <summary>手动开钱箱（不关联订单）前后窗口内同设备没有完成销售或退款。</summary>
    internal static IEnumerable<PosAuditRiskFlag> NoSaleDrawer(IReadOnlyList<PosAuditRiskRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var window = TimeSpan.FromSeconds(options.DrawerCheckoutWindowSeconds);
        var anchors = deviceRows
            .Where(row => Succeeded(row) && (Is(row, C.SaleComplete) || Is(row, C.ReturnRefundComplete)))
            .ToList();
        foreach (var drawer in deviceRows.Where(row => Succeeded(row) && Is(row, C.CashDrawerOpen) && string.IsNullOrWhiteSpace(row.OrderGuid)))
        {
            var previous = anchors.LastOrDefault(row => row.OccurredAtUtc <= drawer.OccurredAtUtc);
            var next = anchors.FirstOrDefault(row => row.OccurredAtUtc >= drawer.OccurredAtUtc);
            var nearest = new[] { previous, next }
                .Where(row => row != null)
                .Select(row => (drawer.OccurredAtUtc - row!.OccurredAtUtc).Duration())
                .DefaultIfEmpty(TimeSpan.MaxValue)
                .Min();
            if (nearest <= window)
            {
                continue;
            }
            var evidence = new Dictionary<string, string>
            {
                ["windowSeconds"] = options.DrawerCheckoutWindowSeconds.ToString(CultureInfo.InvariantCulture),
            };
            AddIfPresent(evidence, "reason", drawer.ReasonCode);
            if (previous != null)
            {
                evidence["previousCheckoutAt"] = Clock(previous.LocalTime);
                evidence["minutesSincePreviousCheckout"] = Minutes(drawer.OccurredAtUtc - previous.OccurredAtUtc);
            }
            if (next != null)
            {
                evidence["nextCheckoutAt"] = Clock(next.LocalTime);
            }
            yield return new PosAuditRiskFlag(drawer, C.Rules.NoSaleDrawer, evidence);
        }
    }

    /// <summary>
    /// 已开始收款（成功加入一笔付款）、销售还没完成时删除商品、清空购物车或减少数量，且应收减少达到下限。
    /// 完成销售、挂单、取单、取消订单、作废都表示这一单已结束，之后的删除不再算。
    /// </summary>
    internal static IEnumerable<PosAuditRiskFlag> DeleteAfterCheckout(IReadOnlyList<PosAuditRiskRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var window = TimeSpan.FromSeconds(options.DeleteAfterCheckoutWindowSeconds);
        PosAuditRiskRow? lastTender = null;
        foreach (var row in deviceRows)
        {
            if (Is(row, C.PaymentTenderAdd))
            {
                if (Succeeded(row))
                {
                    lastTender = row;
                }
            }
            else if (Is(row, C.SaleComplete) || Is(row, C.OrderHold) || Is(row, C.OrderRecall) || Is(row, C.OrderCancel) || Is(row, C.SaleVoid))
            {
                lastTender = null;
            }
            else if (Succeeded(row)
                && (Is(row, C.CartItemRemove) || Is(row, C.CartClear) || Is(row, C.CartItemQuantityChange))
                && lastTender != null
                && row.OccurredAtUtc - lastTender.OccurredAtUtc <= window
                && Reduction(row) is { } deleted
                && deleted >= options.DeleteAfterCheckoutMinAmount)
            {
                var evidence = new Dictionary<string, string>
                {
                    ["anchor"] = "tender",
                    ["checkoutAt"] = Clock(lastTender.LocalTime),
                    ["secondsAfterCheckout"] = ((int)(row.OccurredAtUtc - lastTender.OccurredAtUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    ["deletedAmount"] = Money(deleted),
                };
                AddIfPresent(evidence, "product", row.PrimaryProduct);
                AddIfPresent(evidence, "tenderAmount", lastTender.PaymentAmount is { } paid ? Money(paid) : null);
                AddIfPresent(evidence, "paymentMethod", lastTender.PaymentMethod);
                yield return new PosAuditRiskFlag(row, C.Rules.DeleteAfterCheckout, evidence);
            }
        }
    }

    /// <summary>单品 / 整单折扣或改价降幅达到比例阈值，且让利金额达到下限。</summary>
    internal static IEnumerable<PosAuditRiskFlag> BigDiscount(IEnumerable<PosAuditRiskRow> rows, LegacyEmployeeLogRiskOptions options)
    {
        foreach (var row in rows.Where(Succeeded))
        {
            Dictionary<string, string>? evidence = null;
            if (Is(row, C.CartLineDiscountChange)
                && row.Item is { AfterGrossAmount: > 0, AfterDiscountAmount: { } lineDiscount } item
                && lineDiscount - (item.BeforeDiscountAmount ?? 0m) is var added
                && added >= options.BigDiscountMinAmount
                && lineDiscount / item.AfterGrossAmount.Value * 100m is var linePercent
                && linePercent >= options.BigDiscountMinPercent)
            {
                evidence = new() { ["kind"] = "item", ["percent"] = Percent(linePercent), ["amount"] = Money(added) };
                if (item.BeforeDiscountAmount is > 0 && item.AfterGrossAmount is > 0)
                {
                    evidence["previousPercent"] = Percent(item.BeforeDiscountAmount.Value / item.AfterGrossAmount.Value * 100m);
                }
                AddIfPresent(evidence, "product", item.DisplayName ?? row.PrimaryProduct);
            }
            else if (Is(row, C.CartOrderDiscountChange)
                && row.AfterDiscount is { } cartDiscount
                && row.AfterActual is { } cartActual
                && cartActual + cartDiscount is var gross and > 0
                && cartDiscount - (row.BeforeDiscount ?? 0m) is var cartAdded
                && cartAdded >= options.BigDiscountMinAmount
                && cartDiscount / gross * 100m is var cartPercent
                && cartPercent >= options.BigDiscountMinPercent)
            {
                evidence = new()
                {
                    ["kind"] = "cart",
                    ["percent"] = Percent(cartPercent),
                    ["amount"] = Money(cartAdded),
                    ["originalTotal"] = Money(gross),
                };
                if (row.ProductCount > 0)
                {
                    evidence["itemCount"] = row.ProductCount.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (Is(row, C.CartItemPriceChange)
                && row.Item is { BeforeUnitPrice: > 0, AfterUnitPrice: { } newPrice } priced
                && priced.BeforeUnitPrice.Value is var originalPrice
                && (originalPrice - newPrice) / originalPrice >= options.BigPriceCutMinRatio
                && (originalPrice - newPrice) * (priced.Quantity is > 0 ? priced.Quantity.Value : 1m) is var saving
                && saving >= options.BigDiscountMinAmount)
            {
                evidence = new()
                {
                    ["kind"] = "price",
                    ["originalPrice"] = Money(originalPrice),
                    ["newPrice"] = Money(newPrice),
                    ["percent"] = Percent((originalPrice - newPrice) / originalPrice * 100m),
                    ["amount"] = Money(saving),
                };
                AddIfPresent(evidence, "product", priced.DisplayName ?? row.PrimaryProduct);
            }
            if (evidence != null)
            {
                yield return new PosAuditRiskFlag(row, C.Rules.BigDiscount, evidence);
            }
        }
    }

    /// <summary>同一收银员窗口内删除商品达到 N 次；每段连续删除只标第一次满足条件的那一条。</summary>
    internal static IEnumerable<PosAuditRiskFlag> BurstDelete(IReadOnlyList<PosAuditRiskRow> rows, LegacyEmployeeLogRiskOptions options)
    {
        var window = TimeSpan.FromMinutes(options.BurstDeleteWindowMinutes);
        var n = Math.Max(2, options.BurstDeleteMinCount);
        var byCashier = rows
            .Where(row => Succeeded(row) && Is(row, C.CartItemRemove))
            .GroupBy(row => row.CashierId ?? row.CashierName ?? string.Empty);
        foreach (var group in byCashier)
        {
            var deletes = group.ToList();
            bool Qualifies(int index) => index >= n - 1 && deletes[index].OccurredAtUtc - deletes[index - n + 1].OccurredAtUtc <= window;
            var index = 0;
            while (index < deletes.Count)
            {
                if (!Qualifies(index))
                {
                    index++;
                    continue;
                }
                var start = index - n + 1;
                var end = index;
                while (end + 1 < deletes.Count && Qualifies(end + 1))
                {
                    end++;
                }
                var segment = deletes.GetRange(start, end - start + 1);
                yield return new PosAuditRiskFlag(deletes[index], C.Rules.BurstDelete, new Dictionary<string, string>
                {
                    ["count"] = segment.Count.ToString(CultureInfo.InvariantCulture),
                    ["firstAt"] = Clock(segment[0].LocalTime),
                    ["lastAt"] = Clock(segment[^1].LocalTime),
                    ["totalAmount"] = Money(segment.Sum(row => Reduction(row) ?? 0m)),
                    ["windowMinutes"] = options.BurstDeleteWindowMinutes.ToString(CultureInfo.InvariantCulture),
                    ["threshold"] = n.ToString(CultureInfo.InvariantCulture),
                });
                index = end + 1;
            }
        }
    }

    /// <summary>
    /// 同一张小票重打印达到 N 次，标第 N 次。新收银重打印只打「上一张小票」（不带订单号），
    /// 所以按同设备上一次完成的销售归组；当天还没有销售时按设备 + 本地日期归组。
    /// </summary>
    internal static IEnumerable<PosAuditRiskFlag> RepeatReprint(IReadOnlyList<PosAuditRiskRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var n = Math.Max(2, options.RepeatReprintMinCount);
        PosAuditRiskRow? lastSale = null;
        var groups = new Dictionary<string, (PosAuditRiskRow? Sale, List<PosAuditRiskRow> Reprints)>(StringComparer.Ordinal);
        foreach (var row in deviceRows)
        {
            if (Succeeded(row) && Is(row, C.SaleComplete))
            {
                lastSale = row;
            }
            else if (Succeeded(row) && Is(row, C.ReceiptReprint))
            {
                var key = lastSale != null && lastSale.LocalTime.Date == row.LocalTime.Date
                    ? lastSale.EventId.ToString("N")
                    : "day:" + row.LocalTime.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = (key.StartsWith("day:", StringComparison.Ordinal) ? null : lastSale, new List<PosAuditRiskRow>());
                    groups[key] = group;
                }
                group.Reprints.Add(row);
            }
        }
        foreach (var (sale, reprints) in groups.Values)
        {
            if (reprints.Count < n)
            {
                continue;
            }
            var evidence = new Dictionary<string, string>
            {
                ["count"] = reprints.Count.ToString(CultureInfo.InvariantCulture),
                ["firstAt"] = Clock(reprints[0].LocalTime),
                ["threshold"] = n.ToString(CultureInfo.InvariantCulture),
            };
            if (sale != null)
            {
                AddIfPresent(evidence, "orderId", sale.OrderGuid);
                AddIfPresent(evidence, "amount", sale.PaymentAmount is { } paid ? Money(paid) : null);
                evidence["saleAt"] = Clock(sale.LocalTime);
            }
            yield return new PosAuditRiskFlag(reprints[n - 1], C.Rules.RepeatReprint, evidence);
        }
    }

    /// <summary>营业时间之外（门店墙钟）的操作：每台设备每天的「开门前」「打烊后」两段各标第一条。</summary>
    internal static IEnumerable<PosAuditRiskFlag> OffHours(IReadOnlyList<PosAuditRiskRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var segments = deviceRows
            .Select(row => (Row: row, Segment: LegacyEmployeeLogRiskEvaluator.SegmentOf(row.LocalTime, options)))
            .Where(item => item.Segment != null)
            .GroupBy(item => (item.Row.LocalTime.Date, item.Segment));
        foreach (var group in segments)
        {
            var items = group.Select(item => item.Row).ToList();
            yield return new PosAuditRiskFlag(items[0], C.Rules.OffHours, new Dictionary<string, string>
            {
                ["segment"] = group.Key.Segment!,
                ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                ["firstAt"] = Clock(items[0].LocalTime),
                ["lastAt"] = Clock(items[^1].LocalTime),
                ["open"] = options.BusinessOpen.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                ["close"] = options.BusinessClose.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            });
        }
    }

    /// <summary>紧急覆盖下发生的操作：每条都标，结果（成功 / 被拒 / 失败）写进依据。</summary>
    internal static IEnumerable<PosAuditRiskFlag> EmergencyOverride(IEnumerable<PosAuditRiskRow> rows)
    {
        foreach (var row in rows.Where(row => row.IsEmergencyOverride))
        {
            var evidence = new Dictionary<string, string>
            {
                ["operationType"] = row.OperationType,
                ["outcome"] = row.Outcome,
            };
            AddIfPresent(evidence, "reason", row.ReasonCode);
            yield return new PosAuditRiskFlag(row, C.Rules.EmergencyOverride, evidence);
        }
    }

    /// <summary>购物车操作让应收减少的金额（操作前后实收差额）；增加或算不出时返回 null。</summary>
    internal static decimal? Reduction(PosAuditRiskRow row) =>
        row.BeforeActual is { } before && row.AfterActual is { } after && before - after > 0m ? before - after : null;

    private static bool Is(PosAuditRiskRow row, string operationType) =>
        string.Equals(row.OperationType, operationType, StringComparison.Ordinal);

    private static bool Succeeded(PosAuditRiskRow row) =>
        string.Equals(row.Outcome, C.Succeeded, StringComparison.Ordinal);

    private static void AddIfPresent(Dictionary<string, string> evidence, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            evidence[key] = value.Trim();
        }
    }

    private static string Clock(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) => ((int)Math.Floor(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Percent(decimal value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
}
