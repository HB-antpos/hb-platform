using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AttendanceWorkSessionCalculatorTests
{
    private static readonly AttendanceSchedule Schedule = new()
    {
        ScheduleGuid = "schedule-1",
        StoreCode = "BRI",
        UserGuid = "staff-user",
        WorkDate = new DateTime(2026, 5, 18),
        StartTime = new TimeSpan(9, 0, 0),
        EndTime = new TimeSpan(17, 0, 0),
        Status = "Active",
    };

    [Fact]
    public void Calculate_MultipleSegments_ExcludesBreakAndUsesFirstInAndFinalOut()
    {
        var punches = new[]
        {
            Punch("in-1", "ClockIn", 8, 42),
            Punch("out-1", "ClockOut", 12, 0),
            Punch("in-2", "ClockIn", 13, 0),
            Punch("out-2", "ClockOut", 17, 22),
        };

        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            punches,
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 18, 0, 0),
            earlyLeaveGraceMinutes: 5);

        Assert.Equal("Completed", result.ScheduleState);
        Assert.Equal(2, result.CompletedSegmentCount);
        Assert.Equal(460, result.WorkedMinutes);
        Assert.Equal(60, result.BreakMinutes);
        Assert.Equal(15, result.EarlyOvertimeMinutes);
        Assert.Equal(15, result.LateOvertimeMinutes);
        Assert.Equal(30, result.CandidateOvertimeMinutes);
        Assert.Equal("Break", result.Segments[0].Status);
        Assert.Equal("Final", result.Segments[1].Status);
    }

    [Theory]
    [InlineData(14, 59, 0)]
    [InlineData(15, 0, 15)]
    [InlineData(22, 29, 15)]
    [InlineData(22, 30, 30)]
    [InlineData(37, 29, 30)]
    [InlineData(37, 30, 45)]
    public void RoundOvertimeMinutes_UsesExactNearestQuarterHourWithHalfUp(
        int minutes,
        int seconds,
        int expected)
    {
        Assert.Equal(
            expected,
            AttendanceWorkSessionCalculator.RoundOvertimeMinutes(
                TimeSpan.FromMinutes(minutes).Add(TimeSpan.FromSeconds(seconds))));
    }

    // 门槛 15（默认）/ 30：早到与晚走分别独立判断；达到门槛后仍按 15 分钟一档取整。
    [Theory]
    [InlineData(15, 14, 0)]
    [InlineData(15, 15, 15)]
    [InlineData(15, 29, 30)]
    [InlineData(30, 15, 0)]
    [InlineData(30, 29, 0)]
    [InlineData(30, 30, 30)]
    [InlineData(30, 37, 30)]
    [InlineData(30, 38, 45)]
    [InlineData(30, 60, 60)]
    public void Calculate_OvertimeThreshold_AppliesToEarlyAndLateSeparately(
        int threshold,
        int minutes,
        int expected)
    {
        // 早到 minutes 分钟、晚走 minutes 分钟，两侧用同一门槛。
        var punches = new[]
        {
            Punch("in-1", "ClockIn", 9, 0, -minutes),
            Punch("out-1", "ClockOut", 17, 0, minutes),
        };

        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            punches,
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 18, 30, 0),
            earlyLeaveGraceMinutes: 5,
            overtimeThresholdMinutes: threshold);

        Assert.Equal(expected, result.EarlyOvertimeMinutes);
        Assert.Equal(expected, result.LateOvertimeMinutes);
        Assert.Equal(expected * 2, result.CandidateOvertimeMinutes);
    }

    [Fact]
    public void Calculate_OvertimeThreshold_EarlyAboveThresholdLateBelow_OnlyEarlyCounts()
    {
        var punches = new[]
        {
            Punch("in-1", "ClockIn", 9, 0, -32),
            Punch("out-1", "ClockOut", 17, 0, 20),
        };

        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            punches,
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 18, 30, 0),
            earlyLeaveGraceMinutes: 5,
            overtimeThresholdMinutes: 30);

        Assert.Equal(30, result.EarlyOvertimeMinutes);
        Assert.Equal(0, result.LateOvertimeMinutes);
        Assert.Equal(30, result.CandidateOvertimeMinutes);
    }

    [Fact]
    public void Calculate_WithoutThresholdArgument_KeepsLegacyFifteenMinuteBehavior()
    {
        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            new[] { Punch("in-1", "ClockIn", 9, 0, -15), Punch("out-1", "ClockOut", 17, 0, 15) },
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 18, 30, 0),
            earlyLeaveGraceMinutes: 5);

        Assert.Equal(30, result.CandidateOvertimeMinutes);
    }

    [Fact]
    public void Calculate_EarlyIntermediateClockOutBeforeShiftEnd_RemainsBreakBoundary()
    {
        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            new[] { Punch("in-1", "ClockIn", 9, 0), Punch("out-1", "ClockOut", 12, 0) },
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 12, 0, 0),
            earlyLeaveGraceMinutes: 5);

        Assert.Equal("OnBreak", result.ScheduleState);
        Assert.False(result.HasMissingClockOut);
        Assert.Equal("Break", Assert.Single(result.Segments).Status);
        Assert.Equal(0, result.CandidateOvertimeMinutes);
    }

    [Fact]
    public void Calculate_OpenSegmentAfterShiftEnd_DerivesMissingClockOutWithoutClosingIt()
    {
        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            new[] { Punch("in-1", "ClockIn", 9, 0) },
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 17, 6, 0),
            earlyLeaveGraceMinutes: 5);

        Assert.Equal("MissingClockOut", result.ScheduleState);
        Assert.True(result.HasOpenSegment);
        Assert.True(result.HasMissingClockOut);
    }

    [Fact]
    public void Calculate_EarlyBreakWithoutReturnAfterShiftEnd_BecomesFinalEarlyLeave()
    {
        var result = AttendanceWorkSessionCalculator.Calculate(
            Schedule,
            new[] { Punch("in-1", "ClockIn", 9, 0), Punch("out-1", "ClockOut", 12, 0) },
            segmentLimit: 2,
            nowLocal: new DateTime(2026, 5, 18, 18, 0, 0),
            earlyLeaveGraceMinutes: 5);

        var segment = Assert.Single(result.Segments);
        Assert.Equal("Completed", result.ScheduleState);
        Assert.Equal("Final", segment.Status);
        Assert.Equal("EarlyLeave", segment.ClockOut!.Status);
    }

    [Fact]
    public void Calculate_SydneyDstFallBack_UsesUtcSequenceAndElapsedMinutes()
    {
        var schedule = new AttendanceSchedule
        {
            ScheduleGuid = "sydney-dst",
            StoreCode = "SYD",
            UserGuid = "staff-user",
            WorkDate = new DateTime(2026, 4, 5),
            StartTime = new TimeSpan(1, 0, 0),
            EndTime = new TimeSpan(4, 0, 0),
            Status = "Active",
        };
        var punches = new[]
        {
            // 夏令时回拨后，本地 02:20 发生在本地 02:50 之后；真实顺序只能由 UTC 判断。
            new AttendancePunch
            {
                PunchGuid = "dst-in",
                ScheduleGuid = schedule.ScheduleGuid,
                StoreCode = schedule.StoreCode,
                UserGuid = schedule.UserGuid,
                WorkDate = schedule.WorkDate,
                PunchType = "ClockIn",
                PunchTimeLocal = new DateTime(2026, 4, 5, 2, 50, 0),
                PunchTimeUtc = new DateTime(2026, 4, 4, 15, 50, 0, DateTimeKind.Utc),
            },
            new AttendancePunch
            {
                PunchGuid = "dst-out",
                ScheduleGuid = schedule.ScheduleGuid,
                StoreCode = schedule.StoreCode,
                UserGuid = schedule.UserGuid,
                WorkDate = schedule.WorkDate,
                PunchType = "ClockOut",
                PunchTimeLocal = new DateTime(2026, 4, 5, 2, 20, 0),
                PunchTimeUtc = new DateTime(2026, 4, 4, 16, 20, 0, DateTimeKind.Utc),
            },
        };

        var result = AttendanceWorkSessionCalculator.Calculate(
            schedule,
            punches,
            segmentLimit: 1,
            nowLocal: new DateTime(2026, 4, 5, 4, 30, 0),
            earlyLeaveGraceMinutes: 5);

        var segment = Assert.Single(result.Segments);
        Assert.Equal("dst-in", segment.ClockIn!.PunchGuid);
        Assert.Equal("dst-out", segment.ClockOut!.PunchGuid);
        Assert.Equal(30, segment.DurationMinutes);
        Assert.Equal(30, result.WorkedMinutes);
    }

    private static AttendancePunch Punch(string guid, string type, int hour, int minute, int offsetMinutes = 0) => new()
    {
        PunchGuid = guid,
        ScheduleGuid = Schedule.ScheduleGuid,
        StoreCode = Schedule.StoreCode,
        UserGuid = Schedule.UserGuid,
        WorkDate = Schedule.WorkDate,
        PunchType = type,
        PunchTimeLocal = Schedule.WorkDate.AddHours(hour).AddMinutes(minute + offsetMinutes),
        PunchTimeUtc = Schedule.WorkDate.AddHours(hour - 10).AddMinutes(minute + offsetMinutes),
        Status = "Normal",
    };
}
