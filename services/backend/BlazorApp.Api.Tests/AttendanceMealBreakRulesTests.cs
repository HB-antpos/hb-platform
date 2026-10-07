using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>排班用餐规则的纯逻辑用例：默认次数（与移动端同口径）、应有次数、已处理次数与提醒时间。</summary>
public sealed class AttendanceMealBreakRulesTests
{
    private static readonly DateTime Start = new(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(270, 0)] // 正好 4.5 小时按低一档
    [InlineData(271, 1)]
    [InlineData(480, 1)]
    [InlineData(540, 1)] // 正好 9 小时按低一档
    [InlineData(541, 2)]
    [InlineData(720, 2)]
    public void DefaultCount_ThresholdsAreExclusive(int durationMinutes, int expected) =>
        Assert.Equal(expected, AttendanceMealBreakRules.DefaultCount(durationMinutes));

    [Fact]
    public void ScheduledDurationMinutes_CrossesMidnight()
    {
        Assert.Equal(480, AttendanceMealBreakRules.ScheduledDurationMinutes(
            new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)));
        Assert.Equal(480, AttendanceMealBreakRules.ScheduledDurationMinutes(
            new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0)));
    }

    [Fact]
    public void EffectiveCount_OverrideWinsIncludingZero_AndIsClamped()
    {
        var start = new TimeSpan(9, 0, 0);
        var end = new TimeSpan(17, 0, 0);

        Assert.Equal(1, AttendanceMealBreakRules.EffectiveCount(null, start, end));
        Assert.Equal(0, AttendanceMealBreakRules.EffectiveCount(0, start, end));
        Assert.Equal(2, AttendanceMealBreakRules.EffectiveCount(2, start, end));
        Assert.Equal(3, AttendanceMealBreakRules.EffectiveCount(9, start, end));
        Assert.Equal(0, AttendanceMealBreakRules.EffectiveCount(-1, start, end));
    }

    [Theory]
    [InlineData(1, 239, 0)]
    [InlineData(1, 240, 1)]
    [InlineData(1, 600, 1)] // 不超过排班有效次数
    [InlineData(2, 479, 1)]
    [InlineData(2, 480, 2)]
    [InlineData(0, 600, 0)]
    public void RequiredCount_KthMealNeedsFourHoursTimesK(int effective, int worked, int expected) =>
        Assert.Equal(expected, AttendanceMealBreakRules.RequiredCount(effective, worked));

    [Fact]
    public void DeductionMinutes_IsRequiredCountTimesThirty() =>
        Assert.Equal(60, AttendanceMealBreakRules.DeductionMinutes(2, 500));

    [Fact]
    public void CountQualifyingGapBreaks_OnlyGapsOfThirtyMinutesOrMore()
    {
        var segments = new List<AttendanceShiftSegmentDto>
        {
            Segment(0, 120),
            Segment(150, 300), // 与上一段空档 30 分钟：算
            Segment(320, null), // 与上一段空档 20 分钟：不算
        };

        Assert.Equal(1, AttendanceMealBreakRules.CountQualifyingGapBreaks(segments));
    }

    [Fact]
    public void Evaluate_NoBreaks_AfterFourHours_MissesOne()
    {
        var session = Session(480, Segment(0, 480));

        var snapshot = AttendanceMealBreakRules.Evaluate(1, session, [], 0, Start.AddMinutes(480));

        Assert.Equal(1, snapshot.RequiredCount);
        Assert.Equal(0, snapshot.HandledCount);
        Assert.Equal(1, snapshot.MissingCount);
        Assert.Null(snapshot.NextReminderAtUtc); // 已无进行中班段
    }

    [Fact]
    public void Evaluate_ShortBreakIsNotCounted_LongBreakIs()
    {
        var session = Session(480, Segment(0, 480));
        var shortBreak = Break(120, 129); // 9 分钟：误触
        var longBreak = Break(200, 230);
        var now = Start.AddMinutes(480);

        Assert.Equal(1, AttendanceMealBreakRules.Evaluate(1, session, [shortBreak], 0, now).MissingCount);
        Assert.Equal(0, AttendanceMealBreakRules.Evaluate(1, session, [shortBreak, longBreak], 0, now).MissingCount);
    }

    [Fact]
    public void Evaluate_ClaimedHandledCountSuppressesRepeatedQuestion()
    {
        var session = Session(480, Segment(0, 480));

        var snapshot = AttendanceMealBreakRules.Evaluate(1, session, [], 1, Start.AddMinutes(480));

        Assert.Equal(0, snapshot.MissingCount);
        Assert.Equal(1, snapshot.HandledCount);
    }

    [Fact]
    public void Evaluate_GapBetweenSegmentsCountsAsMeal()
    {
        // 第 1 段 0–240，歇 60 分钟，第 2 段 300–540：中间空档就是午休。
        var session = Session(480, Segment(0, 240), Segment(300, 540));

        var snapshot = AttendanceMealBreakRules.Evaluate(1, session, [], 0, Start.AddMinutes(540));

        Assert.Equal(1, snapshot.GapBreakCount);
        Assert.Equal(0, snapshot.MissingCount);
    }

    [Fact]
    public void Evaluate_OpenSegmentCountsTowardWorkedTime_AndSchedulesReminder()
    {
        // 上班 0 分钟，进行中；现在是第 250 分钟：已满 4 小时、没休息。
        var session = Session(0, hasOpenSegment: true, Segment(0, null));

        var snapshot = AttendanceMealBreakRules.Evaluate(1, session, [], 0, Start.AddMinutes(250));

        Assert.Equal(1, snapshot.RequiredCount);
        Assert.Equal(1, snapshot.MissingCount);
        Assert.Equal(Start.AddMinutes(240), snapshot.NextReminderAtUtc);
    }

    [Fact]
    public void Evaluate_ReminderRestartsFromLastBreakEnd_AndStopsWhenAllMealsHandled()
    {
        var session = Session(0, hasOpenSegment: true, Segment(0, null));
        var firstBreak = Break(240, 270);
        var now = Start.AddMinutes(300);

        var twoMeals = AttendanceMealBreakRules.Evaluate(2, session, [firstBreak], 0, now);
        var oneMeal = AttendanceMealBreakRules.Evaluate(1, session, [firstBreak], 0, now);

        Assert.Equal(Start.AddMinutes(270 + 240), twoMeals.NextReminderAtUtc);
        Assert.Null(oneMeal.NextReminderAtUtc);
    }

    [Fact]
    public void Evaluate_OpenBreakSuppressesReminder_AndIsReported()
    {
        var session = Session(0, hasOpenSegment: true, Segment(0, null));
        var open = new AttendanceMealBreak { StartUtc = Start.AddMinutes(250), EndUtc = null };

        var snapshot = AttendanceMealBreakRules.Evaluate(1, session, [open], 0, Start.AddMinutes(255));

        Assert.True(snapshot.HasOpenBreak);
        Assert.Equal(Start.AddMinutes(250), snapshot.OpenBreakStartUtc);
        Assert.Null(snapshot.NextReminderAtUtc);
    }

    [Fact]
    public void Evaluate_ZeroEffectiveCount_NeverRemindsOrMisses()
    {
        var session = Session(0, hasOpenSegment: true, Segment(0, null));

        var snapshot = AttendanceMealBreakRules.Evaluate(0, session, [], 0, Start.AddMinutes(600));

        Assert.Equal(0, snapshot.MissingCount);
        Assert.Null(snapshot.NextReminderAtUtc);
    }

    // 班段：上班/下班分钟数相对 Start；进行中的班段 clockOutMinutes 为 null。
    private static AttendanceShiftSegmentDto Segment(int clockInMinutes, int? clockOutMinutes) =>
        new()
        {
            ClockIn = new AttendancePunchDto { PunchTimeUtc = Start.AddMinutes(clockInMinutes) },
            ClockOut = clockOutMinutes.HasValue
                ? new AttendancePunchDto { PunchTimeUtc = Start.AddMinutes(clockOutMinutes.Value) }
                : null,
            DurationMinutes = clockOutMinutes.HasValue ? clockOutMinutes.Value - clockInMinutes : null,
        };

    private static AttendanceWorkSessionDto Session(
        int workedMinutes,
        params AttendanceShiftSegmentDto[] segments) =>
        Session(workedMinutes, hasOpenSegment: false, segments);

    private static AttendanceWorkSessionDto Session(
        int workedMinutes,
        bool hasOpenSegment,
        params AttendanceShiftSegmentDto[] segments) =>
        new()
        {
            WorkedMinutes = workedMinutes,
            HasOpenSegment = hasOpenSegment,
            Segments = segments.ToList(),
        };

    private static AttendanceMealBreak Break(int startMinutes, int endMinutes) =>
        new()
        {
            StartUtc = Start.AddMinutes(startMinutes),
            EndUtc = Start.AddMinutes(endMinutes),
        };
}
