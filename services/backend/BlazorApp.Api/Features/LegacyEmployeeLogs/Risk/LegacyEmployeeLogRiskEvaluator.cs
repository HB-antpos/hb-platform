using System.Globalization;
using C = BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskCatalog;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;

/// <summary>扫描读出的一行日志；Detail 只在需要解析详情的操作上有值。时间为门店墙钟。</summary>
public sealed record LegacyLogRow(
    string Id,
    string StoreCode,
    string? DeviceCode,
    string? EmployeeId,
    string? EmployeeName,
    string? Operation,
    DateTime OperationTime,
    string? Detail = null
);

/// <summary>
/// 一次判定的输入：单店、评估窗口 [EvalFrom, EvalTo)。Rows 须覆盖规则所需的上下文：
/// 操作序列从 EvalFrom 往前 <see cref="LegacyEmployeeLogRiskEvaluator.SequenceMargin"/>、往后同样余量；
/// 重打印与非营业时间的行从 EvalFrom 当天零点起。只有落在窗口内的记录会被标记。
/// </summary>
public sealed record LegacyRiskEvaluationInput(
    string StoreCode,
    DateTime EvalFrom,
    DateTime EvalTo,
    IReadOnlyList<LegacyLogRow> Rows
);

public sealed record LegacyRiskFlag(LegacyLogRow Target, string RuleCode, IReadOnlyDictionary<string, string> Evidence);

public sealed record LegacyRiskImpact(LegacyLogRow Target, decimal Amount);

public sealed record LegacyRiskEvaluation(IReadOnlyList<LegacyRiskFlag> Flags, IReadOnlyList<LegacyRiskImpact> Impacts);

/// <summary>
/// 六条异常规则的纯函数判定。每条规则的标记目标都选「稳定」的那一行（例如连续删除中第 N 次、
/// 同一订单第 N 次重打印、非营业时段第一条），日志晚到或重复扫描时目标不漂移，核查结论才挂得住。
/// </summary>
public static class LegacyEmployeeLogRiskEvaluator
{
    /// <summary>操作序列在评估窗口两侧需要的余量：覆盖开钱箱 / 结账后删除窗口，以及连续删除要回看两倍窗口。</summary>
    public static TimeSpan SequenceMargin(LegacyEmployeeLogRiskOptions options) =>
        TimeSpan.FromSeconds(Math.Max(
            Math.Max(options.DrawerCheckoutWindowSeconds, options.DeleteAfterCheckoutWindowSeconds),
            options.BurstDeleteWindowMinutes * 60 * 2
        ) + 60);

    public static LegacyRiskEvaluation Evaluate(LegacyRiskEvaluationInput input, LegacyEmployeeLogRiskOptions options)
    {
        var rows = input.Rows
            .Where(row => row.StoreCode == input.StoreCode)
            .DistinctBy(row => row.Id)
            .OrderBy(row => row.OperationTime)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToList();
        bool InWindow(LegacyLogRow row) => row.OperationTime >= input.EvalFrom && row.OperationTime < input.EvalTo;

        var flags = new List<LegacyRiskFlag>();
        var byDevice = rows.GroupBy(row => row.DeviceCode ?? string.Empty).Select(group => group.ToList()).ToList();
        foreach (var deviceRows in byDevice)
        {
            flags.AddRange(NoSaleDrawer(deviceRows, options).Where(flag => InWindow(flag.Target)));
            flags.AddRange(DeleteAfterCheckout(deviceRows, options).Where(flag => InWindow(flag.Target)));
            flags.AddRange(OffHours(deviceRows, options).Where(flag => InWindow(flag.Target)));
        }
        flags.AddRange(BigDiscount(rows.Where(InWindow), options));
        flags.AddRange(BurstDelete(rows, options).Where(flag => InWindow(flag.Target)));
        flags.AddRange(RepeatReprint(rows, options).Where(flag => InWindow(flag.Target)));

        var impacts = rows
            .Where(row => InWindow(row) && row.Detail != null)
            .Select(row => (Row: row, Amount: LegacyEmployeeLogDetailParser.AmountImpact(row.Operation, row.Detail)))
            .Where(item => item.Amount.HasValue)
            .Select(item => new LegacyRiskImpact(item.Row, item.Amount!.Value))
            .ToList();
        return new LegacyRiskEvaluation(flags, impacts);
    }

    /// <summary>手动开钱箱（旧收银只记录手动开钱箱）前后窗口内同设备没有「结账」。</summary>
    internal static IEnumerable<LegacyRiskFlag> NoSaleDrawer(IReadOnlyList<LegacyLogRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var window = TimeSpan.FromSeconds(options.DrawerCheckoutWindowSeconds);
        var checkouts = deviceRows.Where(row => Is(row, C.Checkout)).Select(row => row.OperationTime).ToList();
        foreach (var drawer in deviceRows.Where(row => Is(row, C.OpenDrawer)))
        {
            var previous = checkouts.Where(time => time <= drawer.OperationTime).Select(time => (DateTime?)time).LastOrDefault();
            var next = checkouts.Where(time => time >= drawer.OperationTime).Select(time => (DateTime?)time).FirstOrDefault();
            var nearest = new[] { previous, next }
                .Where(time => time.HasValue)
                .Select(time => (drawer.OperationTime - time!.Value).Duration())
                .DefaultIfEmpty(TimeSpan.MaxValue)
                .Min();
            if (nearest <= window)
            {
                continue;
            }
            // 旧收银开钱箱前会写一条同时刻的「身份确认」；记下是否有，便于核查时判断是否走了授权。
            var confirmed = deviceRows.Any(row =>
                Is(row, C.IdentityConfirm) && (row.OperationTime - drawer.OperationTime).Duration() <= TimeSpan.FromSeconds(5));
            var evidence = new Dictionary<string, string>
            {
                ["windowSeconds"] = options.DrawerCheckoutWindowSeconds.ToString(CultureInfo.InvariantCulture),
                ["identityConfirmed"] = confirmed ? "true" : "false",
            };
            if (previous is { } prev)
            {
                evidence["previousCheckoutAt"] = Clock(prev);
                evidence["minutesSincePreviousCheckout"] = Minutes(drawer.OperationTime - prev);
            }
            if (next is { } nxt)
            {
                evidence["nextCheckoutAt"] = Clock(nxt);
            }
            yield return new LegacyRiskFlag(drawer, C.Rules.NoSaleDrawer, evidence);
        }
    }

    /// <summary>
    /// 结账开始后、顾客还没换人前删除商品：删除前窗口内同设备有「结账」，其间没有加商品 / 挂单 / 恢复挂单
    /// （出现这些说明已经开始下一单），且删除金额达到下限。
    /// </summary>
    internal static IEnumerable<LegacyRiskFlag> DeleteAfterCheckout(IReadOnlyList<LegacyLogRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var window = TimeSpan.FromSeconds(options.DeleteAfterCheckoutWindowSeconds);
        LegacyLogRow? lastCheckout = null;
        foreach (var row in deviceRows)
        {
            if (Is(row, C.Checkout))
            {
                lastCheckout = row;
            }
            else if (Is(row, C.AddItem) || Is(row, C.AddNoCodeItem) || Is(row, C.Hold) || Is(row, C.ResumeHold))
            {
                lastCheckout = null;
            }
            else if (Is(row, C.DeleteItem) && lastCheckout != null && row.OperationTime - lastCheckout.OperationTime <= window)
            {
                var fields = LegacyEmployeeLogDetailParser.Parse(row.Detail);
                var amount = LegacyEmployeeLogDetailParser.Number(fields, "总金额");
                if (amount is not { } deleted || deleted < options.DeleteAfterCheckoutMinAmount)
                {
                    continue;
                }
                var evidence = new Dictionary<string, string>
                {
                    ["checkoutAt"] = Clock(lastCheckout.OperationTime),
                    ["secondsAfterCheckout"] = ((int)(row.OperationTime - lastCheckout.OperationTime).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    ["deletedAmount"] = Money(deleted),
                };
                AddIfPresent(evidence, "product", LegacyEmployeeLogDetailParser.Text(fields, "从购物车删除商品", "商品"));
                AddIfPresent(evidence, "quantity", LegacyEmployeeLogDetailParser.Text(fields, "数量"));
                yield return new LegacyRiskFlag(row, C.Rules.DeleteAfterCheckout, evidence);
            }
        }
    }

    /// <summary>单品 / 整单折扣达到阈值，或改价降幅达到阈值。</summary>
    internal static IEnumerable<LegacyRiskFlag> BigDiscount(IEnumerable<LegacyLogRow> rows, LegacyEmployeeLogRiskOptions options)
    {
        foreach (var row in rows.Where(row => row.Detail != null))
        {
            var fields = LegacyEmployeeLogDetailParser.Parse(row.Detail);
            Dictionary<string, string>? evidence = null;
            if (Is(row, C.ChangeDiscount)
                && LegacyEmployeeLogDetailParser.Number(fields, "新折扣") is { } itemRate
                && itemRate >= options.BigDiscountMinPercent)
            {
                evidence = new() { ["kind"] = "item", ["percent"] = Percent(itemRate) };
                AddIfPresent(evidence, "previousPercent", LegacyEmployeeLogDetailParser.Number(fields, "原折扣") is { } before ? Percent(before) : null);
            }
            else if (Is(row, C.ChangeAllDiscount)
                && LegacyEmployeeLogDetailParser.Number(fields, "将所有商品折扣率设置为") is { } cartRate
                && cartRate >= options.BigDiscountMinPercent)
            {
                evidence = new() { ["kind"] = "cart", ["percent"] = Percent(cartRate) };
                AddIfPresent(evidence, "itemCount", LegacyEmployeeLogDetailParser.Text(fields, "购物车商品数"));
                AddIfPresent(evidence, "originalTotal", LegacyEmployeeLogDetailParser.Number(fields, "原总金额") is { } total ? Money(total) : null);
            }
            else if (Is(row, C.ChangePrice)
                && LegacyEmployeeLogDetailParser.Number(fields, "原价格") is { } original and > 0
                && LegacyEmployeeLogDetailParser.Number(fields, "新价格") is { } updated
                && (original - updated) / original >= options.BigPriceCutMinRatio)
            {
                evidence = new()
                {
                    ["kind"] = "price",
                    ["originalPrice"] = Money(original),
                    ["newPrice"] = Money(updated),
                    ["percent"] = Percent(Math.Round((original - updated) / original * 100m, 1)),
                };
            }
            if (evidence == null)
            {
                continue;
            }
            AddIfPresent(evidence, "product", LegacyEmployeeLogDetailParser.Text(fields, "商品"));
            yield return new LegacyRiskFlag(row, C.Rules.BigDiscount, evidence);
        }
    }

    /// <summary>
    /// 同一员工窗口内删除达到 N 次。每段连续删除只标一条：第一次满足条件的那一行（即该段的第 N 次删除），
    /// 依据里给出整段的次数、起止时间与金额。
    /// </summary>
    internal static IEnumerable<LegacyRiskFlag> BurstDelete(IReadOnlyList<LegacyLogRow> rows, LegacyEmployeeLogRiskOptions options)
    {
        var window = TimeSpan.FromMinutes(options.BurstDeleteWindowMinutes);
        var n = Math.Max(2, options.BurstDeleteMinCount);
        var byEmployee = rows
            .Where(row => Is(row, C.DeleteItem))
            .GroupBy(row => row.EmployeeId ?? row.EmployeeName ?? string.Empty);
        foreach (var group in byEmployee)
        {
            var deletes = group.ToList();
            bool Qualifies(int index) => index >= n - 1 && deletes[index].OperationTime - deletes[index - n + 1].OperationTime <= window;
            var index = 0;
            while (index < deletes.Count)
            {
                if (!Qualifies(index))
                {
                    index++;
                    continue;
                }
                // 一段：从第一个满足条件的位置一直延伸到不再满足为止。
                var start = index - n + 1;
                var end = index;
                while (end + 1 < deletes.Count && Qualifies(end + 1))
                {
                    end++;
                }
                var segment = deletes.GetRange(start, end - start + 1);
                var total = segment.Sum(row => LegacyEmployeeLogDetailParser.Number(LegacyEmployeeLogDetailParser.Parse(row.Detail), "总金额") ?? 0m);
                yield return new LegacyRiskFlag(deletes[index], C.Rules.BurstDelete, new Dictionary<string, string>
                {
                    ["count"] = segment.Count.ToString(CultureInfo.InvariantCulture),
                    ["firstAt"] = Clock(segment[0].OperationTime),
                    ["lastAt"] = Clock(segment[^1].OperationTime),
                    ["totalAmount"] = Money(total),
                    ["windowMinutes"] = options.BurstDeleteWindowMinutes.ToString(CultureInfo.InvariantCulture),
                    ["threshold"] = n.ToString(CultureInfo.InvariantCulture),
                });
                index = end + 1;
            }
        }
    }

    /// <summary>同一订单同一天重打印达到 N 次，标第 N 次那一行。</summary>
    internal static IEnumerable<LegacyRiskFlag> RepeatReprint(IReadOnlyList<LegacyLogRow> rows, LegacyEmployeeLogRiskOptions options)
    {
        var n = Math.Max(2, options.RepeatReprintMinCount);
        var groups = rows
            .Where(row => Is(row, C.Reprint))
            .Select(row => (Row: row, Order: LegacyEmployeeLogDetailParser.OrderId(row.Detail)))
            .Where(item => item.Order != null)
            .GroupBy(item => (item.Order, item.Row.OperationTime.Date));
        foreach (var group in groups)
        {
            var reprints = group.Select(item => item.Row).ToList();
            if (reprints.Count < n)
            {
                continue;
            }
            var evidence = new Dictionary<string, string>
            {
                ["orderId"] = group.Key.Order!,
                ["count"] = reprints.Count.ToString(CultureInfo.InvariantCulture),
                ["firstAt"] = Clock(reprints[0].OperationTime),
                ["threshold"] = n.ToString(CultureInfo.InvariantCulture),
            };
            AddIfPresent(evidence, "amount", LegacyEmployeeLogDetailParser.Number(LegacyEmployeeLogDetailParser.Parse(reprints[0].Detail), "金额") is { } amount ? Money(amount) : null);
            yield return new LegacyRiskFlag(reprints[n - 1], C.Rules.RepeatReprint, evidence);
        }
    }

    /// <summary>营业时间之外的操作：每台设备每天的「开门前」「打烊后」两段各标第一条。</summary>
    internal static IEnumerable<LegacyRiskFlag> OffHours(IReadOnlyList<LegacyLogRow> deviceRows, LegacyEmployeeLogRiskOptions options)
    {
        var segments = deviceRows
            .Select(row => (Row: row, Segment: SegmentOf(row.OperationTime, options)))
            .Where(item => item.Segment != null)
            .GroupBy(item => (item.Row.OperationTime.Date, item.Segment));
        foreach (var group in segments)
        {
            var items = group.Select(item => item.Row).ToList();
            yield return new LegacyRiskFlag(items[0], C.Rules.OffHours, new Dictionary<string, string>
            {
                ["segment"] = group.Key.Segment!,
                ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                ["firstAt"] = Clock(items[0].OperationTime),
                ["lastAt"] = Clock(items[^1].OperationTime),
                ["open"] = options.BusinessOpen.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                ["close"] = options.BusinessClose.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            });
        }
    }

    internal static string? SegmentOf(DateTime time, LegacyEmployeeLogRiskOptions options)
    {
        var clock = time.TimeOfDay;
        if (clock < options.BusinessOpen)
        {
            return "beforeOpen";
        }
        return clock >= options.BusinessClose ? "afterClose" : null;
    }

    private static bool Is(LegacyLogRow row, string operation) =>
        string.Equals(row.Operation?.Trim(), operation, StringComparison.Ordinal);

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

    private static string Percent(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
