using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 排班用餐规则（后端口径）。默认次数规则与移动端 attendance-my-week.ts 保持一致：
/// 班次超过 9 小时 2 次、超过 4.5 小时 1 次，每次 30 分钟；排班上指定过次数（含 0）就用指定值。
/// 此前只有移动端会算，后端不知道有效次数，所以下班检查、工时扣除都要在这里再算一遍。
/// </summary>
public static class AttendanceMealBreakRules
{
    /// <summary>每次用餐扣除的分钟数。</summary>
    public const int MealBreakMinutes = 30;

    /// <summary>店长可指定的用餐次数上限（与排班校验一致）。</summary>
    public const int MaxMealBreakCount = 3;

    /// <summary>每累计工作满 4 小时需要一次用餐：既是提醒线，也是下班检查线（早于 4.5 小时扣除线和 5 小时餐休上限）。</summary>
    public const int WorkMinutesPerMeal = 240;

    /// <summary>时长不足该值的休息记录视为误触，不算一次用餐。</summary>
    public const int QualifyingBreakMinutes = 10;

    /// <summary>相邻班段之间的空档达到该值，本身就算一次用餐，不必再追问。</summary>
    public const int QualifyingGapMinutes = 30;

    public const string ClaimStatusNone = "None";
    public const string ClaimStatusPending = "Pending";
    public const string ClaimStatusApproved = "Approved";
    public const string ClaimStatusRejected = "Rejected";
    public const string ClaimStatusCancelled = "Cancelled";

    /// <summary>班次计划时长（分钟），跨午夜时加 24 小时。</summary>
    public static int ScheduledDurationMinutes(TimeSpan startTime, TimeSpan endTime)
    {
        var minutes = (int)(endTime - startTime).TotalMinutes;
        return endTime > startTime ? minutes : minutes + 1440;
    }

    /// <summary>按班次时长推算的默认用餐次数；门槛均为「超过」，正好 4.5 / 9 小时按低一档。</summary>
    public static int DefaultCount(int durationMinutes)
    {
        if (durationMinutes > 9 * 60) return 2;
        if (durationMinutes > 270) return 1;
        return 0;
    }

    /// <summary>排班的有效用餐次数：店长指定过（含 0＝取消）用指定值，否则按时长默认。</summary>
    public static int EffectiveCount(int? mealBreakCount, TimeSpan startTime, TimeSpan endTime) =>
        Math.Clamp(
            mealBreakCount ?? DefaultCount(ScheduledDurationMinutes(startTime, endTime)),
            0,
            MaxMealBreakCount);

    /// <summary>实际已工作 workedMinutes 后应当用餐的次数：第 k 次要求累计工作满 4 小时 × k，且不超过排班有效次数。</summary>
    public static int RequiredCount(int effectiveCount, int workedMinutes) =>
        Math.Min(Math.Max(0, effectiveCount), Math.Max(0, workedMinutes) / WorkMinutesPerMeal);

    /// <summary>计薪时从工时里扣除的用餐分钟数；是否休息过都按排班扣，未休息的差额靠审批加回。</summary>
    public static int DeductionMinutes(int effectiveCount, int workedMinutes) =>
        RequiredCount(effectiveCount, workedMinutes) * MealBreakMinutes;

    /// <summary>相邻班段之间满 30 分钟的空档个数；每个空档算一次已用餐。</summary>
    public static int CountQualifyingGapBreaks(IReadOnlyList<AttendanceShiftSegmentDto> segments)
    {
        var count = 0;
        for (var index = 1; index < segments.Count; index++)
        {
            var previousOut = segments[index - 1].ClockOut?.PunchTimeUtc;
            var nextIn = segments[index].ClockIn?.PunchTimeUtc;
            if (previousOut.HasValue
                && nextIn.HasValue
                && (nextIn.Value - previousOut.Value).TotalMinutes >= QualifyingGapMinutes)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 评估某个排班此刻的用餐状态。
    /// breaks 是该排班下的休息记录；claimedHandledCount 是此前下班声明已处理过的次数（不含 Cancelled），
    /// 声明“休息了”和“没休息”都算已处理，避免同一天后续下班重复追问同一次用餐。
    /// </summary>
    public static AttendanceMealSnapshot Evaluate(
        int effectiveCount,
        AttendanceWorkSessionDto session,
        IReadOnlyCollection<AttendanceMealBreak> breaks,
        int claimedHandledCount,
        DateTime nowUtc)
    {
        var endedBreaks = breaks.Where(item => item.EndUtc.HasValue).ToList();
        var qualifyingBreaks = endedBreaks.Count(item =>
            (item.EndUtc!.Value - item.StartUtc).TotalMinutes >= QualifyingBreakMinutes);
        var openBreak = breaks.FirstOrDefault(item => !item.EndUtc.HasValue);
        var gapBreaks = CountQualifyingGapBreaks(session.Segments);
        var handled = qualifyingBreaks + gapBreaks + Math.Max(0, claimedHandledCount);

        // WorkedMinutes 只含已闭合班段；此刻下班时还要把进行中班段的时长算进去。
        var openSegment = session.HasOpenSegment
            ? session.Segments.LastOrDefault(item => item.ClockOut == null && item.ClockIn != null)
            : null;
        var openMinutes = openSegment?.ClockIn == null
            ? 0
            : Math.Max(0, (int)(nowUtc - openSegment.ClockIn.PunchTimeUtc).TotalMinutes);
        var projectedWorked = session.WorkedMinutes + openMinutes;
        var required = RequiredCount(effectiveCount, projectedWorked);

        DateTime? nextReminderAtUtc = null;
        if (effectiveCount > 0
            && openSegment?.ClockIn != null
            && openBreak == null
            && handled < effectiveCount)
        {
            // 连续工作从上班或上一次休息结束起算，满 4 小时提醒下一次。
            var basis = openSegment.ClockIn.PunchTimeUtc;
            foreach (var item in endedBreaks.Where(item => item.EndUtc!.Value > basis))
            {
                basis = item.EndUtc!.Value;
            }
            nextReminderAtUtc = basis.AddMinutes(WorkMinutesPerMeal);
        }

        return new AttendanceMealSnapshot(
            effectiveCount,
            qualifyingBreaks,
            gapBreaks,
            Math.Max(0, claimedHandledCount),
            handled,
            required,
            Math.Max(0, required - handled),
            projectedWorked,
            openBreak?.StartUtc,
            nextReminderAtUtc);
    }
}

/// <summary>某个排班此刻的用餐状态（只读快照）。</summary>
public sealed record AttendanceMealSnapshot(
    int EffectiveCount,
    int QualifyingBreakCount,
    int GapBreakCount,
    int ClaimedHandledCount,
    int HandledCount,
    int RequiredCount,
    int MissingCount,
    int ProjectedWorkedMinutes,
    DateTime? OpenBreakStartUtc,
    DateTime? NextReminderAtUtc)
{
    public bool HasOpenBreak => OpenBreakStartUtc.HasValue;
}
