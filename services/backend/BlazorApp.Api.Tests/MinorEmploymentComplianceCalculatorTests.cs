using BlazorApp.Api.Services.Attendance;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class MinorEmploymentComplianceCalculatorTests
{
    [Theory]
    [InlineData(6, 8, false)]
    [InlineData(15, 16, true)]
    [InlineData(16, 17, false)]
    public void Evaluate_SchoolCommuteOnlyAppliesToAfterSchoolStart(int start, int end, bool expected)
    {
        var day = new DateTime(2026, 9, 14);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new()
        {
            Profile = new MinorEmploymentProfile
            {
                WorkState = "NSW", DateOfBirth = new DateTime(2010, 1, 1),
                CommuteFromSchoolMinutes = 30,
                RequiredSchoolDays = new Dictionary<DateTime, MinorEmploymentSchoolDay>
                {
                    [day] = new() { IsRequired = true, Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(15) },
                },
            },
            WorkItems = [Shift("HB", day, start, end)],
        });
        Assert.Equal(expected, result.Findings.Any(item => item.RuleId == "COMMUTE_SCHOOL_CONFLICT"));
    }

    [Fact]
    public void Evaluate_QldSchoolWeekCountsExternalWorkAndUsesSundayWindow()
    {
        var sunday = new DateTime(2026, 9, 13);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = QldProfile(sunday),
            WorkItems = new[]
            {
                Shift("other", sunday, 9, 12),
                Shift("HB", sunday.AddDays(2), 16, 19),
                Shift("HB", sunday.AddDays(4), 16, 19),
                Shift("HB", sunday.AddDays(6), 14, 18),
            },
        });

        var finding = Assert.Single(result.Findings.Where(item => item.RuleId == "QLD_SCHOOL_WEEKLY_LIMIT"));
        Assert.Equal(780, finding.ActualMinutes);
        Assert.Equal(720, finding.LimitMinutes);
    }

    [Fact]
    public void Evaluate_QldSchoolDayWarnsAtFourHoursAndOneMinute()
    {
        var day = new DateTime(2026, 9, 14);
        var profile = QldProfile(day);
        var calendar = profile.RequiredSchoolDays.ToDictionary(item => item.Key, item => item.Value);
        calendar[day] = new MinorEmploymentSchoolDay { IsRequired = true, Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(15) };
        profile = new MinorEmploymentProfile { DateOfBirth = profile.DateOfBirth, WorkState = "QLD", IsQldSchoolAgedChild = true, ConsentStatus = "Signed", ReviewStatus = "Approved", AwardCode = "MA000004", ExternalWorkKnown = true, RequiredSchoolDays = calendar };

        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput { Profile = profile, WorkItems = new[] { Shift("HB", day, 16, 20, 241) } });
        Assert.Contains(result.Findings, item => item.RuleId == "QLD_SCHOOL_DAY_DAILY_LIMIT" && item.ActualMinutes == 241 && item.LimitMinutes == 240);
    }

    [Fact]
    public void Evaluate_NswDoesNotApplyQldNumericLimits()
    {
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = new MinorEmploymentProfile { WorkState = "NSW", DateOfBirth = new DateTime(2010, 1, 1), ConsentStatus = "Signed", ReviewStatus = "Approved", AwardCode = "MA000004", ExternalWorkKnown = true },
            WorkItems = new[] { Shift("HB", new DateTime(2026, 9, 14), 8, 20) },
        });
        Assert.Contains(result.Findings, item => item.RuleId == "NSW_AWARD_REVIEW");
        Assert.DoesNotContain(result.Findings, item => item.RuleId.StartsWith("QLD_", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_NswStillWarnsWhenShiftConflictsWithSchoolAttendance()
    {
        var day = new DateTime(2026, 9, 14);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = new MinorEmploymentProfile
            {
                DateOfBirth = new DateTime(2010, 1, 1), WorkState = "NSW", ConsentStatus = "signed", ReviewStatus = "approved", AwardCode = "MA000004", ExternalWorkKnown = true,
                RequiredSchoolDays = new Dictionary<DateTime, MinorEmploymentSchoolDay> { [day] = new() { IsRequired = true, Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(15) } },
            },
            WorkItems = new[] { Shift("HB", day, 14, 17) },
        });
        Assert.Contains(result.Findings, item => item.RuleId == "SCHOOL_ATTENDANCE_CONFLICT");
        Assert.DoesNotContain(result.Findings, item => item.RuleId.StartsWith("QLD_SCHOOL_DAY", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_QldUnknownSchoolCalendarStillWarnsForNightWork()
    {
        var day = new DateTime(2026, 9, 14);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = new MinorEmploymentProfile { DateOfBirth = new DateTime(2011, 1, 1), WorkState = "QLD", IsQldSchoolAgedChild = true, ConsentStatus = "signed", ReviewStatus = "approved", AwardCode = "MA000004", ExternalWorkKnown = true },
            WorkItems = new[] { Shift("HB", day, 21, 23) },
        });
        Assert.Contains(result.Findings, item => item.RuleId == "QLD_NIGHT_WORK");
        Assert.Contains(result.Findings, item => item.RuleId == "SCHOOL_CALENDAR_MISSING");
    }

    [Fact]
    public void Evaluate_QldCrossMidnightSplitsDayAndNightBoundaries()
    {
        var sunday = new DateTime(2026, 9, 13);
        var profile = QldProfile(sunday);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = profile,
            WorkItems = new[] { new MinorEmploymentWorkItem { EmployerId = "HB", WorkDate = sunday.AddDays(1), StartLocal = sunday.AddDays(1).AddHours(18), EndLocal = sunday.AddDays(2).AddHours(1) } },
        });
        Assert.Contains(result.Findings, item => item.RuleId == "QLD_NIGHT_WORK");
        Assert.Contains(result.Findings, item => item.RuleId == "QLD_SCHOOL_DAY_DAILY_LIMIT" && item.WorkDate == sunday.AddDays(1) && item.ActualMinutes == 360);
    }

    [Fact]
    public void Evaluate_TargetWeekUsesBoundaryShiftForRestButExcludesItFromWeeklyHours()
    {
        var sunday = new DateTime(2026, 9, 13);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = QldProfile(sunday), TargetWeekStart = sunday,
            WorkItems = new[]
            {
                Shift("HB", sunday.AddDays(-1), 20, 23),
                Shift("HB", sunday, 6, 10),
            },
        });
        Assert.Contains(result.Findings, item => item.RuleId == "QLD_SAME_EMPLOYER_REST");
        Assert.DoesNotContain(result.Findings, item => item.RuleId == "QLD_SCHOOL_WEEKLY_LIMIT");
    }

    [Fact]
    public void Evaluate_ActualFourHourSegmentWithoutAwardRequestsReviewWithoutClaimingException()
    {
        var sunday = new DateTime(2026, 9, 13);
        var profile = QldProfile(sunday);
        profile = new MinorEmploymentProfile { DateOfBirth = profile.DateOfBirth, WorkState = profile.WorkState, IsQldSchoolAgedChild = true, ConsentStatus = "Signed", ReviewStatus = "Approved", ExternalWorkKnown = true, RequiredSchoolDays = profile.RequiredSchoolDays };
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = profile, TargetWeekStart = sunday,
            WorkItems = new[] { new MinorEmploymentWorkItem { EmployerId = "HB", WorkDate = sunday, StartLocal = sunday.AddHours(10), EndLocal = sunday.AddHours(14), ActualWorkedMinutes = 240, IsActual = true } },
        });
        Assert.Contains(result.Findings, item => item.RuleId == "AWARD_BREAK_REVIEW" && item.RuleCategory == "award");
    }

    [Fact]
    public void Evaluate_OngoingActualAndRemainingPlanKeepTailAndLaterShiftInDailyTotal()
    {
        var sunday = new DateTime(2026, 9, 13);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = QldProfile(sunday), TargetWeekStart = sunday,
            WorkItems = new[]
            {
                new MinorEmploymentWorkItem { EmployerId = "HB", ScheduleGuid = "shift-1", WorkDate = sunday, StartLocal = sunday.AddHours(10), EndLocal = sunday.AddHours(12), ActualWorkedMinutes = 120, IsActual = true, IsOngoing = true },
                new MinorEmploymentWorkItem { EmployerId = "HB", ScheduleGuid = "shift-1", WorkDate = sunday, StartLocal = sunday.AddHours(12), EndLocal = sunday.AddHours(16) },
                new MinorEmploymentWorkItem { EmployerId = "HB", ScheduleGuid = "shift-2", WorkDate = sunday, StartLocal = sunday.AddHours(17), EndLocal = sunday.AddHours(21) },
            },
        });

        var finding = Assert.Single(result.Findings.Where(item => item.RuleId == "QLD_NON_SCHOOL_DAY_DAILY_LIMIT"));
        Assert.Equal(600, finding.ActualMinutes); // 进行中 2h + 原班剩余 4h + 当日独立未来班次 4h。
        Assert.Contains(result.Findings, item => item.RuleId == "QLD_SAME_EMPLOYER_REST"); // 独立未来班次仍参与同雇主间隔核验。
    }

    [Fact]
    public void Evaluate_NswActualFourHoursWithoutAwardRequestsAwardReviewOnly()
    {
        var day = new DateTime(2026, 9, 14);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = new MinorEmploymentProfile { DateOfBirth = new DateTime(2011, 1, 1), WorkState = "NSW", ConsentStatus = "Signed", ReviewStatus = "Approved", ExternalWorkKnown = true },
            WorkItems = new[] { new MinorEmploymentWorkItem { EmployerId = "HB", WorkDate = day, StartLocal = day.AddHours(10), EndLocal = day.AddHours(14), ActualWorkedMinutes = 240, IsActual = true } },
        });

        Assert.Contains(result.Findings, item => item.RuleId == "AWARD_BREAK_REVIEW" && item.RuleCategory == "award");
        Assert.DoesNotContain(result.Findings, item => item.RuleId.StartsWith("QLD_", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_AggregateOtherWorkContributesHoursButDoesNotInventTimeBasedRisks()
    {
        var sunday = new DateTime(2026, 9, 13);
        var monday = sunday.AddDays(1);
        var profile = QldProfile(sunday);
        var calendar = profile.RequiredSchoolDays.ToDictionary(item => item.Key, item => item.Value);
        calendar[monday] = new MinorEmploymentSchoolDay { IsRequired = true, Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(15) };
        profile = new MinorEmploymentProfile { DateOfBirth = profile.DateOfBirth, WorkState = "QLD", IsQldSchoolAgedChild = true, ConsentStatus = "Signed", ReviewStatus = "Approved", AwardCode = "MA000004", ExternalWorkKnown = true, RequiredSchoolDays = calendar };
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = profile, TargetWeekStart = sunday,
            WorkItems = new[] { new MinorEmploymentWorkItem { EmployerId = "external:weekly:example", WorkDate = monday, StartLocal = monday, EndLocal = monday.AddHours(3), ActualWorkedMinutes = 180, IsTimeKnown = false } },
        });

        Assert.DoesNotContain(result.Findings, item => item.RuleId is "SCHOOL_ATTENDANCE_CONFLICT" or "QLD_NIGHT_WORK" or "QLD_SAME_EMPLOYER_REST");
    }

    [Fact]
    public void Evaluate_NswCrossMidnightShiftKeepsFullDateForReturnTravelReview()
    {
        var day = new DateTime(2026, 9, 14);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            AssessmentDate = day,
            Profile = new MinorEmploymentProfile
            {
                DateOfBirth = new DateTime(2011, 1, 1), WorkState = "NSW", ConsentStatus = "Signed", ReviewStatus = "Approved", AwardCode = "MA000004", ExternalWorkKnown = true,
                LatestWorkEndLocalTime = new TimeSpan(21, 30, 0), HomewardMinutes = 30, LatestTransportLocalTime = new TimeSpan(22, 0, 0),
            },
            WorkItems = new[] { Shift("HB", day, 20, 24) },
        });

        Assert.Contains(result.Findings, item => item.RuleId == "COMMUTE_LATEST_END_CONFLICT");
        Assert.Contains(result.Findings, item => item.RuleId == "COMMUTE_HOMEWARD_CONFLICT");
    }

    [Fact]
    public void Evaluate_UnknownExternalIntervalsDoNotInventSameEmployerRest()
    {
        var sunday = new DateTime(2026, 9, 13);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = QldProfile(sunday), AssessmentDate = sunday, TargetWeekStart = sunday,
            WorkItems = new[]
            {
                Shift("external:planned", sunday, 10, 11),
                Shift("external:actual", sunday, 12, 13),
            },
        });

        Assert.DoesNotContain(result.Findings, item => item.RuleId == "QLD_SAME_EMPLOYER_REST");
    }

    [Fact]
    public void Evaluate_QldUnderThirteenRequestsRetailAgeReviewWithoutBlocking()
    {
        var sunday = new DateTime(2026, 9, 13);
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = new MinorEmploymentProfile { DateOfBirth = new DateTime(2014, 9, 14), WorkState = "QLD", IsQldSchoolAgedChild = true, ConsentStatus = "Signed", ReviewStatus = "Approved", AwardCode = "MA000004", ExternalWorkKnown = true, RequiredSchoolDays = QldProfile(sunday).RequiredSchoolDays },
            TargetWeekStart = sunday,
            WorkItems = new[] { Shift("HB", sunday, 10, 12) },
        });

        Assert.Contains(result.Findings, item => item.RuleId == "QLD_ORDINARY_RETAIL_MINIMUM_AGE" && item.Severity == "review");
    }

    [Fact]
    public void Evaluate_AdultDoesNotReceiveMinorProfileReminder()
    {
        var result = MinorEmploymentComplianceCalculator.Evaluate(new MinorEmploymentComplianceInput
        {
            Profile = new MinorEmploymentProfile { DateOfBirth = new DateTime(2000, 1, 1), WorkState = "QLD" },
            WorkItems = new[] { Shift("HB", new DateTime(2026, 9, 14), 9, 17) },
        });
        Assert.Empty(result.Findings);
    }

    private static MinorEmploymentProfile QldProfile(DateTime sunday)
    {
        return new MinorEmploymentProfile
        {
            DateOfBirth = new DateTime(2011, 1, 1), WorkState = "QLD", IsQldSchoolAgedChild = true,
            ConsentStatus = "Signed", ReviewStatus = "Approved", AwardCode = "MA000004", ExternalWorkKnown = true,
            RequiredSchoolDays = Enumerable.Range(0, 7).ToDictionary(offset => sunday.AddDays(offset), offset => new MinorEmploymentSchoolDay { IsRequired = offset is > 0 and < 6 }),
        };
    }
    private static MinorEmploymentWorkItem Shift(string employer, DateTime day, int startHour, int endHour, int? actual = null) => new() { EmployerId = employer, WorkDate = day, StartLocal = day.AddHours(startHour), EndLocal = day.AddHours(endHour), ActualWorkedMinutes = actual };
}
