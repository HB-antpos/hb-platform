using BlazorApp.Api.Services.Attendance;

namespace BlazorApp.Api.Services.Myob;

/// <summary>参与转账装填的工时种类（比 <see cref="PayrollBucket"/> 细：带薪假拆成四种，才能映射到不同的 MYOB 类别）。</summary>
internal enum MyobWorkKind
{
    Weekday,
    Saturday,
    Sunday,

    /// <summary>公假上班（按平日类别推送，装填顺序在平日之前）。</summary>
    PublicHolidayWork,

    /// <summary>带薪公假（按平日类别推送）。</summary>
    PublicHolidayPay,

    /// <summary>病假（推送为 Personal Leave Pay）。</summary>
    Sick,

    /// <summary>年假（推送为 Holiday Leave Pay）。</summary>
    Annual,

    /// <summary>年假折现：占用转账额度，但不推送（财务在 MYOB 手工处理）。</summary>
    AnnualCashOut,
}

/// <summary>某员工在某公司名下的一条工时（分钟），Date 为门店本地日期。</summary>
internal sealed record MyobWorkItem(string ConnectionGuid, string StoreCode, DateTime Date, MyobWorkKind Kind, int Minutes);

/// <summary>要推送的一行：公司 × 日期 × MYOB 类别键（<see cref="MyobPayCategoryKeys"/>）× 分钟。</summary>
internal sealed record MyobPlanLine(string ConnectionGuid, DateTime Date, string CategoryKey, int Minutes);

internal sealed class MyobCompanyPlan
{
    public string ConnectionGuid { get; init; } = string.Empty;

    /// <summary>本公司名下实际工时（分钟）。</summary>
    public int ActualMinutes { get; init; }

    /// <summary>本公司分到的转账额度（分钟）：全转账 = 实际工时，全现金 = 0，转账 + 现金 = 按工时比例分摊后的份额。</summary>
    public int CapacityMinutes { get; init; }

    /// <summary>年假折现里占用了转账额度、但不推送的分钟。</summary>
    public int CashOutTransferMinutes { get; init; }

    /// <summary>要推送的行（已按日期、类别合并）。</summary>
    public List<MyobPlanLine> Pushed { get; init; } = [];

    /// <summary>每天每种工时的实际 / 转账 / 现金分钟，供按日明细展示。</summary>
    public Dictionary<(DateTime Date, MyobWorkKind Kind), int> ActualByDayKind { get; init; } = new();

    public Dictionary<(DateTime Date, MyobWorkKind Kind), int> CashByDayKind { get; init; } = new();

    public int PushedMinutes => Pushed.Sum(line => line.Minutes);
}

internal sealed class MyobPlanResult
{
    public Dictionary<string, MyobCompanyPlan> Companies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Timesheet 推送规划（方案第 7、8 节），纯函数、无 IO：
/// 1. 员工在多家发薪公司名下都有工时时，**按各公司的实际工时比例分摊**固定转账工时（只分转账额度，工时本身整体归各自公司）；
///    全转账 = 每家都全进转账，全现金 = 不转账。分摊用分钟取整，余数按「工时多的公司先得、同量按公司标识」逐分钟补齐，结果确定、不会丢分钟。
/// 2. 每家公司在分到的额度里按 <b>周日 → 周六 → 带薪公假 → 病假 → 年假 → 年假折现 → 公假上班 → 平日</b> 装入转账，
///    同一种内按日期从早到晚、同日按分店；某种全部进转账就按实际日期原样，只部分进入就从早到晚填满。
/// 3. 类别：平日 / 公假上班 / 带薪公假 → 平日；周六、周日各自一类；病假 → Personal Leave Pay；年假 → Holiday Leave Pay；
///    年假折现占用额度但不推送。
/// 与 <see cref="PayrollCalculator"/> 的区别：它按档位汇总整期；这里要按天，且要先跨公司分摊。单公司员工两者的转账总工时一致。
/// </summary>
internal static class MyobTimesheetPlanner
{
    /// <summary>装填顺序（先进转账的在前）。</summary>
    internal static readonly IReadOnlyList<MyobWorkKind> FillOrder =
    [
        MyobWorkKind.Sunday,
        MyobWorkKind.Saturday,
        MyobWorkKind.PublicHolidayPay,
        MyobWorkKind.Sick,
        MyobWorkKind.Annual,
        MyobWorkKind.AnnualCashOut,
        MyobWorkKind.PublicHolidayWork,
        MyobWorkKind.Weekday,
    ];

    /// <summary>工时种类 → MYOB 类别键；年假折现不推送返回 null。</summary>
    internal static string? CategoryOf(MyobWorkKind kind) => kind switch
    {
        MyobWorkKind.Weekday or MyobWorkKind.PublicHolidayWork or MyobWorkKind.PublicHolidayPay => MyobPayCategoryKeys.Weekday,
        MyobWorkKind.Saturday => MyobPayCategoryKeys.Saturday,
        MyobWorkKind.Sunday => MyobPayCategoryKeys.Sunday,
        MyobWorkKind.Sick => MyobPayCategoryKeys.PersonalLeave,
        MyobWorkKind.Annual => MyobPayCategoryKeys.HolidayLeave,
        _ => null,
    };

    internal static string KindKey(MyobWorkKind kind) => kind switch
    {
        MyobWorkKind.Weekday => MyobKindKeys.Weekday,
        MyobWorkKind.Saturday => MyobKindKeys.Saturday,
        MyobWorkKind.Sunday => MyobKindKeys.Sunday,
        MyobWorkKind.PublicHolidayWork => MyobKindKeys.PublicHolidayWork,
        MyobWorkKind.PublicHolidayPay => MyobKindKeys.PublicHolidayPay,
        MyobWorkKind.Sick => MyobKindKeys.Sick,
        MyobWorkKind.Annual => MyobKindKeys.Annual,
        _ => MyobKindKeys.AnnualCashOut,
    };

    /// <param name="payMode">Transfer / Cash / Mixed（<see cref="PayrollCalculator.PayModeTransfer"/> 等）。</param>
    /// <param name="fixedTransferHoursPerWeek">转账 + 现金时每周固定转账工时；其他模式忽略。</param>
    /// <param name="weeks">工资周期周数（1 或 2）：固定转账容量 = 每周工时 × 周数。</param>
    /// <param name="items">员工本期在所有公司名下的全部工时（分钟）；分钟 ≤ 0 的忽略。</param>
    public static MyobPlanResult Plan(string payMode, decimal fixedTransferHoursPerWeek, int weeks, IEnumerable<MyobWorkItem> items)
    {
        var positive = items.Where(item => item.Minutes > 0).ToList();
        var byCompany = positive
            .GroupBy(item => item.ConnectionGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var actual = byCompany.ToDictionary(pair => pair.Key, pair => pair.Value.Sum(item => item.Minutes), StringComparer.OrdinalIgnoreCase);
        var capacity = AllocateCapacity(payMode, fixedTransferHoursPerWeek, weeks, actual);

        var result = new MyobPlanResult();
        foreach (var (connection, companyItems) in byCompany.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            result.Companies[connection] = PlanCompany(connection, companyItems, capacity[connection]);
        }

        return result;
    }

    /// <summary>各公司分到的转账额度（分钟）。</summary>
    internal static Dictionary<string, int> AllocateCapacity(
        string payMode,
        decimal fixedTransferHoursPerWeek,
        int weeks,
        IReadOnlyDictionary<string, int> actualByCompany)
    {
        var result = actualByCompany.ToDictionary(pair => pair.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        if (payMode == PayrollCalculator.PayModeTransfer)
        {
            foreach (var pair in actualByCompany)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }

        if (payMode != PayrollCalculator.PayModeMixed)
        {
            return result; // 全现金（或未知模式）：不转账
        }

        var total = actualByCompany.Values.Sum();
        var budget = (int)Math.Round(
            Math.Max(0m, fixedTransferHoursPerWeek) * Math.Max(1, weeks) * 60m, MidpointRounding.AwayFromZero);
        budget = Math.Min(budget, total);
        if (budget <= 0 || total <= 0)
        {
            return result;
        }

        // 工时多的公司先得；同量按公司标识，保证结果确定。
        var ordered = actualByCompany
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var assigned = 0;
        foreach (var pair in ordered)
        {
            var share = (int)((long)budget * pair.Value / total);
            result[pair.Key] = share;
            assigned += share;
        }

        // 取整余下的分钟，逐分钟补给还没装满的公司。
        var remainder = budget - assigned;
        while (remainder > 0)
        {
            var progressed = false;
            foreach (var pair in ordered)
            {
                if (remainder == 0)
                {
                    break;
                }

                if (result[pair.Key] < pair.Value)
                {
                    result[pair.Key]++;
                    remainder--;
                    progressed = true;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        return result;
    }

    private static MyobCompanyPlan PlanCompany(string connection, List<MyobWorkItem> items, int capacityMinutes)
    {
        var actualByDayKind = new Dictionary<(DateTime, MyobWorkKind), int>();
        foreach (var item in items)
        {
            var key = (item.Date.Date, item.Kind);
            actualByDayKind[key] = actualByDayKind.GetValueOrDefault(key) + item.Minutes;
        }

        var left = capacityMinutes;
        var pushedByKey = new Dictionary<(DateTime Date, string Category), int>();
        var transferByDayKind = new Dictionary<(DateTime, MyobWorkKind), int>();
        var cashOutTransfer = 0;
        foreach (var kind in FillOrder)
        {
            foreach (var item in items
                         .Where(item => item.Kind == kind)
                         .OrderBy(item => item.Date)
                         .ThenBy(item => item.StoreCode, StringComparer.OrdinalIgnoreCase))
            {
                if (left <= 0)
                {
                    break;
                }

                var take = Math.Min(item.Minutes, left);
                left -= take;
                var dayKind = (item.Date.Date, kind);
                transferByDayKind[dayKind] = transferByDayKind.GetValueOrDefault(dayKind) + take;
                var category = CategoryOf(kind);
                if (category is null)
                {
                    cashOutTransfer += take;
                    continue;
                }

                var key = (item.Date.Date, category);
                pushedByKey[key] = pushedByKey.GetValueOrDefault(key) + take;
            }
        }

        var cashByDayKind = new Dictionary<(DateTime, MyobWorkKind), int>();
        foreach (var (dayKind, minutes) in actualByDayKind)
        {
            var cash = minutes - transferByDayKind.GetValueOrDefault(dayKind);
            if (cash > 0)
            {
                cashByDayKind[dayKind] = cash;
            }
        }

        return new MyobCompanyPlan
        {
            ConnectionGuid = connection,
            ActualMinutes = items.Sum(item => item.Minutes),
            CapacityMinutes = capacityMinutes,
            CashOutTransferMinutes = cashOutTransfer,
            Pushed = pushedByKey
                .OrderBy(pair => pair.Key.Date)
                .ThenBy(pair => Array.IndexOf(MyobPayCategoryKeys.All, pair.Key.Category))
                .Select(pair => new MyobPlanLine(connection, pair.Key.Date, pair.Key.Category, pair.Value))
                .ToList(),
            ActualByDayKind = actualByDayKind,
            CashByDayKind = cashByDayKind,
        };
    }

    /// <summary>分钟 → 小时（2 位小数，四舍五入）。</summary>
    internal static decimal Hours(int minutes) => PayrollCalculator.RoundHours(minutes);
}
