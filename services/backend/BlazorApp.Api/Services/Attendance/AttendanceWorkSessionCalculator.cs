using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Attendance;

public static class AttendanceWorkSessionCalculator
{
    public static AttendanceWorkSessionDto Calculate(
        AttendanceSchedule schedule,
        IEnumerable<AttendancePunch> sourcePunches,
        int segmentLimit,
        DateTime nowLocal,
        int earlyLeaveGraceMinutes,
        int lateGraceMinutes = 5)
    {
        var punches = sourcePunches
            .Where(item => !item.IsDeleted && item.ScheduleGuid == schedule.ScheduleGuid)
            // 打卡先后和实际工时必须遵循同一条 UTC 时间线；夏令时回拨会让本地墙钟倒退。
            .OrderBy(item => item.PunchTimeUtc)
            .ThenBy(item => item.Id)
            .ToList();
        var supersededPunchGuids = punches
            .Where(item => !string.IsNullOrWhiteSpace(item.SupersedesPunchGuid))
            .Select(item => item.SupersedesPunchGuid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        punches = punches
            .Where(item => !supersededPunchGuids.Contains(item.PunchGuid))
            .ToList();

        var result = new AttendanceWorkSessionDto { SegmentLimit = segmentLimit };
        AttendanceShiftSegmentDto? openSegment = null;
        foreach (var punch in punches)
        {
            if (punch.PunchType.Equals("ClockIn", StringComparison.OrdinalIgnoreCase))
            {
                if (openSegment != null)
                {
                    continue;
                }

                openSegment = new AttendanceShiftSegmentDto
                {
                    SegmentIndex = result.Segments.Count + 1,
                    ClockIn = AttendancePunchProjection.ToDto(punch),
                    Status = "Open",
                };
                result.Segments.Add(openSegment);
                continue;
            }

            if (!punch.PunchType.Equals("ClockOut", StringComparison.OrdinalIgnoreCase)
                || openSegment == null)
            {
                continue;
            }

            openSegment.ClockOut = AttendancePunchProjection.ToDto(punch);
            // 午夜自动下班卡的时间是系统补的 24:00，不是真实下班时间：该段不计时长（不计薪），等店长填实际时间。
            openSegment.DurationMinutes = punch.IsSystemAutoClockOut
                ? 0
                : Math.Max(
                    0,
                    (int)Math.Round(
                        (punch.PunchTimeUtc - openSegment.ClockIn!.PunchTimeUtc).TotalMinutes,
                        MidpointRounding.AwayFromZero));
            openSegment.Status = "Break";
            openSegment = null;
        }

        result.HasOpenSegment = openSegment != null;
        result.CompletedSegmentCount = result.Segments.Count(item => item.ClockOut != null);
        result.WorkedMinutes = result.Segments.Sum(item => item.DurationMinutes ?? 0);
        for (var index = 1; index < result.Segments.Count; index++)
        {
            var previousOut = result.Segments[index - 1].ClockOut?.PunchTimeUtc;
            var nextIn = result.Segments[index].ClockIn?.PunchTimeUtc;
            if (previousOut.HasValue && nextIn.HasValue)
            {
                result.BreakMinutes += Math.Max(
                    0,
                    (int)Math.Round(
                        (nextIn.Value - previousOut.Value).TotalMinutes,
                        MidpointRounding.AwayFromZero));
            }
        }

        var scheduledEnd = schedule.WorkDate.Date.Add(schedule.EndTime);
        // 最后一段由系统自动下班卡闭合 = 员工其实漏打下班：不能定稿（否则 24:00 会算成迟走和加班），按漏下班处理。
        var autoClosedBySystem = result.Segments.Count > 0
            && result.Segments[^1].ClockOut?.IsSystemAutoClockOut == true;
        var canFinalize = !result.HasOpenSegment
            && !autoClosedBySystem
            && result.CompletedSegmentCount > 0
            && (result.CompletedSegmentCount >= segmentLimit
                || result.Segments[^1].ClockOut!.PunchTimeLocal
                    >= scheduledEnd.Subtract(TimeSpan.FromMinutes(earlyLeaveGraceMinutes))
                || nowLocal >= scheduledEnd.AddMinutes(earlyLeaveGraceMinutes));
        if (canFinalize)
        {
            result.Segments[^1].Status = "Final";
            var finalClockOutDto = result.Segments[^1].ClockOut!;
            finalClockOutDto.Status = finalClockOutDto.PunchTimeLocal
                    < scheduledEnd.Subtract(TimeSpan.FromMinutes(earlyLeaveGraceMinutes))
                ? "EarlyLeave"
                : finalClockOutDto.PunchTimeLocal - scheduledEnd >= TimeSpan.FromMinutes(15)
                    ? "LateLeave"
                    : "Normal";
        }

        var scheduledStart = schedule.WorkDate.Date.Add(schedule.StartTime);
        var firstClockInDto = result.Segments.FirstOrDefault()?.ClockIn;
        if (firstClockInDto != null)
        {
            var startDeltaMinutes = RoundActualMinutes(firstClockInDto.PunchTimeLocal - scheduledStart);
            firstClockInDto.EarlyArrivalMinutes = Math.Max(0, -startDeltaMinutes);
            firstClockInDto.LateMinutes = Math.Max(0, startDeltaMinutes);
            firstClockInDto.Status = firstClockInDto.PunchTimeLocal
                    > scheduledStart.AddMinutes(lateGraceMinutes)
                ? "Late"
                : scheduledStart - firstClockInDto.PunchTimeLocal >= TimeSpan.FromMinutes(15)
                    ? "Early"
                    : "Normal";
        }
        foreach (var laterSegment in result.Segments.Skip(1))
        {
            if (laterSegment.ClockIn != null)
            {
                laterSegment.ClockIn.Status = "Normal";
            }
        }
        foreach (var breakSegment in result.Segments.Where(item => item.Status == "Break"))
        {
            if (breakSegment.ClockOut != null)
            {
                breakSegment.ClockOut.Status = "Break";
            }
        }
        var firstClockIn = result.Segments.FirstOrDefault()?.ClockIn?.PunchTimeLocal;
        var finalClockOut = canFinalize ? result.Segments[^1].ClockOut?.PunchTimeLocal : null;
        if (canFinalize && result.Segments[^1].ClockOut is { } boundaryClockOutDto)
        {
            var endDeltaMinutes = RoundActualMinutes(boundaryClockOutDto.PunchTimeLocal - scheduledEnd);
            boundaryClockOutDto.EarlyLeaveMinutes = Math.Max(0, -endDeltaMinutes);
            boundaryClockOutDto.LateDepartureMinutes = Math.Max(0, endDeltaMinutes);
        }
        result.EarlyOvertimeMinutes = firstClockIn.HasValue
            ? RoundOvertimeMinutes(scheduledStart - firstClockIn.Value)
            : 0;
        result.LateOvertimeMinutes = finalClockOut.HasValue
            ? RoundOvertimeMinutes(finalClockOut.Value - scheduledEnd)
            : 0;
        result.CandidateOvertimeMinutes = result.EarlyOvertimeMinutes + result.LateOvertimeMinutes;
        result.HasMissingClockOut = autoClosedBySystem
            || (result.HasOpenSegment
                && nowLocal > scheduledEnd.AddMinutes(earlyLeaveGraceMinutes));
        result.ScheduleState = result.HasMissingClockOut
            ? "MissingClockOut"
            : result.HasOpenSegment
                ? "Working"
                : canFinalize
                    ? "Completed"
                    : result.CompletedSegmentCount > 0
                        ? "OnBreak"
                        : "NotStarted";
        return result;
    }

    private static int RoundActualMinutes(TimeSpan value) =>
        (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);

    public static int RoundOvertimeMinutes(int actualMinutes)
        => RoundOvertimeMinutes(TimeSpan.FromMinutes(actualMinutes));

    public static int RoundOvertimeMinutes(TimeSpan actual)
    {
        if (actual < TimeSpan.FromMinutes(15))
        {
            return 0;
        }

        return (int)(Math.Floor(actual.TotalMinutes / 15d + 0.5d) * 15);
    }
}

internal static class AttendancePunchProjection
{
    internal static AttendancePunchDto ToDto(AttendancePunch item) => new()
    {
        PunchGuid = item.PunchGuid,
        ScheduleGuid = item.ScheduleGuid,
        StoreCode = item.StoreCode,
        UserGuid = item.UserGuid,
        WorkDate = item.WorkDate,
        StoreTimeZone = item.StoreTimeZone,
        PunchType = item.PunchType,
        PunchTimeUtc = item.PunchTimeUtc,
        PunchTimeLocal = item.PunchTimeLocal,
        Status = item.Status,
        Source = item.Source,
        SupersedesPunchGuid = item.SupersedesPunchGuid,
        AdjustmentGuid = item.AdjustmentGuid,
        IsSystemAutoClockOut = item.IsSystemAutoClockOut,
    };
}
