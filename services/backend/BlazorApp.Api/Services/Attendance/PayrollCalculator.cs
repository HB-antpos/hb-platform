using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>工资表的工时档位：平日 / 周六 / 周日 / 公假四档按实际工时计；带薪假（病假、年假、带薪公假、年假发放）单列一档。</summary>
public enum PayrollBucket
{
    Weekday,
    Saturday,
    Sunday,
    PublicHoliday,
    Leave,
}

/// <summary>一个已闭合（或系统自动闭合）的打卡段，时间为门店本地墙钟。</summary>
public sealed record PayrollSegment(DateTime StartLocal, DateTime EndLocal, bool IsSystemAutoClockOut = false);

/// <summary>某个排班当天的计薪输入。</summary>
public sealed record PayrollDayInput(
    DateTime ScheduleStartLocal,
    DateTime ScheduleEndLocal,
    IReadOnlyList<PayrollSegment> Segments,
    int EffectiveMealBreakCount,
    int ApprovedOvertimeMinutes = 0,
    int ApprovedMealAddBackMinutes = 0);

/// <summary>当天计薪结果（分钟）。</summary>
public sealed record PayrollDayResult(
    int WorkedMinutes,
    int InScheduleMinutes,
    int MealDeductionMinutes,
    int MealAddBackMinutes,
    int OvertimeMinutes,
    int PaidMinutes);

/// <summary>某天、某档的计薪工时（分钟），工资按这一天有效的时薪记录定价。</summary>
public sealed record PayrollWorkItem(DateTime Date, PayrollBucket Bucket, int Minutes);

/// <summary>带薪假工时（小时，已是十进制）；Date 用来挑当天有效的时薪记录。</summary>
public sealed record PayrollLeaveItem(DateTime Date, decimal Hours);

/// <summary>工资计算用的时薪记录（EmployeePayRate 的纯数据投影）。</summary>
public sealed record PayrollRate(
    string RateKey,
    DateTime EffectiveFrom,
    string PayMode,
    decimal FixedTransferHoursPerWeek,
    decimal? TransferWeekdayRate,
    decimal? TransferSaturdayRate,
    decimal? TransferSundayRate,
    decimal? TransferPublicHolidayRate,
    decimal? CashRate,
    decimal CashAllowancePerPeriod,
    decimal? MinimumRate)
{
    public static PayrollRate From(EmployeePayRate item) => new(
        item.RateGuid,
        item.EffectiveFrom.Date,
        item.PayMode,
        item.FixedTransferHoursPerWeek,
        item.TransferWeekdayRate,
        item.TransferSaturdayRate,
        item.TransferSundayRate,
        item.TransferPublicHolidayRate,
        item.CashRate,
        item.CashAllowancePerPeriod,
        item.MinimumRate);
}

public sealed class PayrollPayInput
{
    public DateTime PeriodStart { get; init; }
    public int Weeks { get; init; } = 1;
    public IReadOnlyList<PayrollRate> Rates { get; init; } = Array.Empty<PayrollRate>();
    public IReadOnlyList<PayrollWorkItem> WorkItems { get; init; } = Array.Empty<PayrollWorkItem>();
    public IReadOnlyList<PayrollLeaveItem> LeaveItems { get; init; } = Array.Empty<PayrollLeaveItem>();
}

/// <summary>按档位汇总的小时数（转账 / 现金桶用同一形状）。</summary>
public sealed class PayrollBucketHours
{
    public decimal Weekday { get; set; }
    public decimal Saturday { get; set; }
    public decimal Sunday { get; set; }
    public decimal PublicHoliday { get; set; }
    public decimal Leave { get; set; }

    public decimal Total => Weekday + Saturday + Sunday + PublicHoliday + Leave;

    internal void Add(PayrollBucket bucket, decimal hours)
    {
        switch (bucket)
        {
            case PayrollBucket.Weekday: Weekday += hours; break;
            case PayrollBucket.Saturday: Saturday += hours; break;
            case PayrollBucket.Sunday: Sunday += hours; break;
            case PayrollBucket.PublicHoliday: PublicHoliday += hours; break;
            default: Leave += hours; break;
        }
    }
}

public sealed class PayrollPayResult
{
    /// <summary>本期主记录（期末当天有效的那条）决定发放方式、固定转账工时和补贴；没有任何记录时为 null。</summary>
    public string? PayMode { get; init; }
    public decimal FixedTransferHoursPerWeek { get; init; }
    public decimal TransferCapacityHours { get; init; }
    public PayrollBucketHours Hours { get; init; } = new();
    public PayrollBucketHours TransferHours { get; init; } = new();
    public PayrollBucketHours CashHours { get; init; } = new();
    public decimal TransferAmount { get; init; }
    public decimal CashAmount { get; init; }
    /// <summary>现金单一时薪（主记录）；仅展示，计算按每天有效记录。</summary>
    public decimal? CashRate { get; init; }
    public decimal Allowance { get; init; }
    public decimal GrossPay { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// 工时统计与工资的纯计算（规格 docs/plans/2026-10-21-attendance-payroll-leave.md 第 0.2、3 节，后端为准）：
/// - 当天计薪分钟 = Σ 已闭合段与排班窗口的重叠 + 已批准加班 − 扣餐 + 已批准加回；系统自动下班段不计；
/// - 扣餐 = max(0, min(排班有效次数, ⌊实际工时 / 4h⌋) − 段间满 30 分钟空档数) × 30；
/// - 小时 = round(分钟 / 60, 2)，金额 = round(小时 × 时薪, 2)，逐档相加，一律四舍五入（AwayFromZero）；
/// - 周末、公假按实际工时不乘系数，转账时薪分四档，带薪假用平日转账时薪；现金时薪只有一个价；
/// - 转账 + 现金：转账容量 = 固定工时/周 × 周数，按 周日 → 周六 → 带薪假 → 公假 → 平日 依次装入，剩下的 × 现金时薪，再 ± 补贴。
/// 不读数据库，所有输入由服务层准备好。
/// </summary>
public static class PayrollCalculator
{
    public const string PayModeTransfer = "Transfer";
    public const string PayModeCash = "Cash";
    public const string PayModeMixed = "Mixed";

    /// <summary>年假（休息 / 发放）每个工作日记 8 h（10-26 定）。</summary>
    public const int AnnualLeaveHoursPerWorkday = 8;

    public const string WarningNoRate = "NO_RATE";
    public const string WarningMissingTransferRate = "MISSING_TRANSFER_RATE";
    public const string WarningMissingCashRate = "MISSING_CASH_RATE";
    public const string WarningBelowMinimumRate = "BELOW_MINIMUM_RATE";

    /// <summary>转账装填顺序（10-26 定）。</summary>
    public static readonly IReadOnlyList<PayrollBucket> TransferFillOrder = new[]
    {
        PayrollBucket.Sunday,
        PayrollBucket.Saturday,
        PayrollBucket.Leave,
        PayrollBucket.PublicHoliday,
        PayrollBucket.Weekday,
    };

    public static bool IsValidPayMode(string? payMode) =>
        payMode is PayModeTransfer or PayModeCash or PayModeMixed;

    /// <summary>分钟 → 十进制小时，最多 2 位小数（7:59 → 7.98）。</summary>
    public static decimal RoundHours(int minutes) =>
        Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    public static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>档位：公假优先，其次按门店本地日期的星期。</summary>
    public static PayrollBucket ResolveTier(DateTime workDate, bool isPublicHoliday) =>
        isPublicHoliday
            ? PayrollBucket.PublicHoliday
            : workDate.DayOfWeek switch
            {
                DayOfWeek.Saturday => PayrollBucket.Saturday,
                DayOfWeek.Sunday => PayrollBucket.Sunday,
                _ => PayrollBucket.Weekday,
            };

    /// <summary>区间内（含首尾）周一至周五且不是公共假期的天数。</summary>
    public static int CountLeaveWorkdays(DateTime startDate, DateTime endDate, ISet<DateTime> publicHolidays)
    {
        var count = 0;
        for (var day = startDate.Date; day <= endDate.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
                && !publicHolidays.Contains(day))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>年假休息 / 发放的申请工时 = 区间内工作日（排除公假）× 8 h。</summary>
    public static decimal AnnualLeaveRequestedHours(DateTime startDate, DateTime endDate, ISet<DateTime> publicHolidays) =>
        CountLeaveWorkdays(startDate, endDate, publicHolidays) * AnnualLeaveHoursPerWorkday;

    /// <summary>当天计薪分钟。PaidMinutes（排班 DTO 上的旧字段）的含义不变，这里是工资口径的独立计算。</summary>
    public static PayrollDayResult CalculateDay(PayrollDayInput input)
    {
        var segments = input.Segments.OrderBy(item => item.StartLocal).ToList();
        var worked = 0;
        var inSchedule = 0;
        foreach (var segment in segments)
        {
            // 午夜自动下班段的 24:00 不是真实下班时间：既不算实际工时，也不算排班内工时。
            if (segment.IsSystemAutoClockOut || segment.EndLocal <= segment.StartLocal)
            {
                continue;
            }
            worked += RoundMinutes(segment.EndLocal - segment.StartLocal);
            var overlapStart = segment.StartLocal > input.ScheduleStartLocal ? segment.StartLocal : input.ScheduleStartLocal;
            var overlapEnd = segment.EndLocal < input.ScheduleEndLocal ? segment.EndLocal : input.ScheduleEndLocal;
            if (overlapEnd > overlapStart)
            {
                inSchedule += RoundMinutes(overlapEnd - overlapStart);
            }
        }

        var gaps = 0;
        for (var index = 1; index < segments.Count; index++)
        {
            if ((segments[index].StartLocal - segments[index - 1].EndLocal).TotalMinutes
                >= AttendanceMealBreakRules.QualifyingGapMinutes)
            {
                gaps++;
            }
        }

        var deduction = AttendanceMealBreakRules.DeductionMinutes(input.EffectiveMealBreakCount, worked, gaps);
        // 加回不能超过扣除（与 ApplyMealPayFields 同口径）。
        var addBack = Math.Clamp(input.ApprovedMealAddBackMinutes, 0, deduction);
        var overtime = Math.Max(0, input.ApprovedOvertimeMinutes);
        var paid = Math.Max(0, inSchedule + overtime - deduction + addBack);
        return new PayrollDayResult(worked, inSchedule, deduction, addBack, overtime, paid);
    }

    /// <summary>当天时薪记录：生效日期不晚于该天的最新一条。</summary>
    public static PayrollRate? ResolveRate(IReadOnlyList<PayrollRate> rates, DateTime date) =>
        rates
            .Where(item => item.EffectiveFrom.Date <= date.Date)
            .OrderByDescending(item => item.EffectiveFrom)
            .FirstOrDefault();

    public static PayrollPayResult CalculatePay(PayrollPayInput input)
    {
        var weeks = Math.Max(1, input.Weeks);
        var periodEnd = input.PeriodStart.Date.AddDays(weeks * 7 - 1);
        var warnings = new List<string>();
        // 主记录：期末当天有效的那条；没有时退到本期内最早生效的那条（期中才开始有时薪）。
        var primary = ResolveRate(input.Rates, periodEnd)
            ?? input.Rates.OrderBy(item => item.EffectiveFrom).FirstOrDefault();

        // 按 (档位, 当天有效时薪记录) 分组：同一组先合计再舍到 2 位小数。只有一条时薪记录时与样例逐档口径完全一致。
        var groups = new Dictionary<(PayrollBucket Bucket, string RateKey), PayrollGroup>();
        foreach (var item in input.WorkItems.Where(item => item.Minutes > 0))
        {
            var rate = ResolveRate(input.Rates, item.Date);
            var group = GetGroup(groups, item.Bucket, rate);
            group.Minutes += item.Minutes;
        }
        foreach (var item in input.LeaveItems.Where(item => item.Hours > 0))
        {
            var rate = ResolveRate(input.Rates, item.Date);
            var group = GetGroup(groups, PayrollBucket.Leave, rate);
            group.LeaveHours += item.Hours;
        }
        foreach (var group in groups.Values)
        {
            group.Hours = group.Bucket == PayrollBucket.Leave
                ? Round2(group.LeaveHours) + RoundHours(group.Minutes)
                : RoundHours(group.Minutes);
        }

        var hours = new PayrollBucketHours();
        foreach (var group in groups.Values)
        {
            hours.Add(group.Bucket, group.Hours);
        }

        if (primary == null)
        {
            if (groups.Count > 0)
            {
                warnings.Add(WarningNoRate);
            }
            return new PayrollPayResult
            {
                Hours = hours,
                CashHours = new PayrollBucketHours(),
                TransferHours = new PayrollBucketHours(),
                Warnings = warnings,
            };
        }

        if (groups.Values.Any(item => item.Rate == null))
        {
            warnings.Add(WarningNoRate);
        }

        var payMode = IsValidPayMode(primary.PayMode) ? primary.PayMode : PayModeTransfer;
        var capacity = payMode switch
        {
            PayModeTransfer => decimal.MaxValue,
            PayModeCash => 0m,
            _ => Round2(Math.Max(0, primary.FixedTransferHoursPerWeek) * weeks),
        };

        var left = capacity;
        var transferHours = new PayrollBucketHours();
        var cashHours = new PayrollBucketHours();
        var transferAmount = 0m;
        var cashHoursByRate = new Dictionary<string, (PayrollRate? Rate, decimal Hours)>();
        foreach (var bucket in TransferFillOrder)
        {
            // 同一档内按时薪记录的生效先后装入（多条记录时早的先进转账）。
            foreach (var group in groups.Values
                .Where(item => item.Bucket == bucket)
                .OrderBy(item => item.Rate?.EffectiveFrom ?? DateTime.MaxValue))
            {
                var take = Round2(Math.Min(group.Hours, Math.Max(0, left)));
                var cash = Round2(group.Hours - take);
                left -= take;
                transferHours.Add(bucket, take);
                cashHours.Add(bucket, cash);
                if (take > 0)
                {
                    var rate = ResolveTransferRate(group.Rate, bucket);
                    if (rate == null)
                    {
                        AddWarning(warnings, group.Rate == null ? WarningNoRate : WarningMissingTransferRate);
                    }
                    else
                    {
                        transferAmount += Round2(take * rate.Value);
                    }
                }
                if (cash > 0)
                {
                    var key = group.RateKey;
                    var existingHours = cashHoursByRate.TryGetValue(key, out var existing) ? existing.Hours : 0m;
                    cashHoursByRate[key] = (group.Rate, existingHours + cash);
                }
            }
        }

        // 现金单一时薪：同一条时薪记录下的现金工时先合计（舍 2 位）再乘现金时薪。
        var cashAmount = 0m;
        foreach (var (rate, cashTotal) in cashHoursByRate.Values)
        {
            if (rate?.CashRate is { } cashRate)
            {
                cashAmount += Round2(Round2(cashTotal) * cashRate);
            }
            else
            {
                AddWarning(warnings, rate == null ? WarningNoRate : WarningMissingCashRate);
            }
        }

        var usedRates = groups.Values
            .Select(item => item.Rate)
            .Append(primary)
            .Where(item => item != null)
            .Select(item => item!)
            .DistinctBy(item => item.RateKey)
            .ToList();
        if (usedRates.Any(IsBelowMinimum))
        {
            warnings.Add(WarningBelowMinimumRate);
        }

        transferAmount = Round2(transferAmount);
        cashAmount = Round2(cashAmount);
        var allowance = Round2(primary.CashAllowancePerPeriod);
        return new PayrollPayResult
        {
            PayMode = payMode,
            FixedTransferHoursPerWeek = primary.FixedTransferHoursPerWeek,
            TransferCapacityHours = payMode == PayModeTransfer ? hours.Total : capacity,
            Hours = hours,
            TransferHours = transferHours,
            CashHours = cashHours,
            TransferAmount = transferAmount,
            CashAmount = cashAmount,
            CashRate = primary.CashRate,
            Allowance = allowance,
            GrossPay = Round2(transferAmount + cashAmount + allowance),
            Warnings = warnings,
        };
    }

    /// <summary>转账时薪：带薪假用平日转账时薪。</summary>
    private static decimal? ResolveTransferRate(PayrollRate? rate, PayrollBucket bucket) => rate == null
        ? null
        : bucket switch
        {
            PayrollBucket.Saturday => rate.TransferSaturdayRate,
            PayrollBucket.Sunday => rate.TransferSundayRate,
            PayrollBucket.PublicHoliday => rate.TransferPublicHolidayRate,
            _ => rate.TransferWeekdayRate,
        };

    /// <summary>任一实际会用到的时薪低于自填的最低时薪就提示（全现金只看现金时薪，全转账只看转账四档）。</summary>
    private static bool IsBelowMinimum(PayrollRate rate)
    {
        if (rate.MinimumRate is not { } minimum)
        {
            return false;
        }
        var candidates = new List<decimal?>();
        if (rate.PayMode != PayModeCash)
        {
            candidates.AddRange(new[]
            {
                rate.TransferWeekdayRate,
                rate.TransferSaturdayRate,
                rate.TransferSundayRate,
                rate.TransferPublicHolidayRate,
            });
        }
        if (rate.PayMode != PayModeTransfer)
        {
            candidates.Add(rate.CashRate);
        }
        return candidates.Any(item => item.HasValue && item.Value < minimum);
    }

    private static void AddWarning(List<string> warnings, string code)
    {
        if (!warnings.Contains(code))
        {
            warnings.Add(code);
        }
    }

    private static PayrollGroup GetGroup(
        Dictionary<(PayrollBucket Bucket, string RateKey), PayrollGroup> groups,
        PayrollBucket bucket,
        PayrollRate? rate)
    {
        var key = (bucket, rate?.RateKey ?? string.Empty);
        if (!groups.TryGetValue(key, out var group))
        {
            group = new PayrollGroup(bucket, rate);
            groups[key] = group;
        }
        return group;
    }

    private static int RoundMinutes(TimeSpan value) =>
        (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);

    private sealed class PayrollGroup(PayrollBucket bucket, PayrollRate? rate)
    {
        public PayrollBucket Bucket { get; } = bucket;
        public PayrollRate? Rate { get; } = rate;
        public string RateKey => Rate?.RateKey ?? string.Empty;
        public int Minutes { get; set; }
        public decimal LeaveHours { get; set; }
        public decimal Hours { get; set; }
    }
}
