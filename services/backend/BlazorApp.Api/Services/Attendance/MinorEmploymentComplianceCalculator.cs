namespace BlazorApp.Api.Services.Attendance;

/// <summary>无状态的未成年用工提醒计算。调用方不得以计算结果阻断保存、发布或打卡。</summary>
public static class MinorEmploymentComplianceCalculator
{
    public const string QueenslandChildEmploymentUrl = "https://www.legislation.qld.gov.au/view/whole/html/inforce/current/sl-2016-0137";

    public static MinorEmploymentComplianceEvaluation Evaluate(MinorEmploymentComplianceInput input)
    {
        var findings = new List<MinorEmploymentComplianceFinding>();
        var shifts = input.WorkItems.Where(item => item.EndLocal > item.StartLocal).ToList();
        var assessmentDate = input.AssessmentDate?.Date ?? (shifts.Count == 0 ? DateTime.Today : shifts.Min(item => item.WorkDate.Date));
        var profile = input.Profile;
        if (profile == null)
        {
            findings.Add(Incomplete("PROFILE_MISSING", "未找到未成年合规档案，无法确认学校、签署和外部工作安排。"));
            return new(findings);
        }
        if (IsAdult(profile, assessmentDate)) return new(findings);

        AddProfileFindings(profile, findings);
        var stateIsQld = string.Equals(profile.WorkState, "QLD", StringComparison.OrdinalIgnoreCase);
        // HB 默认普通零售；QLD 未满 13 岁需特殊许可/例外核验。此提示不阻断排班。
        if (stateIsQld && profile.DateOfBirth.HasValue && profile.DateOfBirth.Value.Date.AddYears(13) > assessmentDate)
            findings.Add(Review("QLD_ORDINARY_RETAIL_MINIMUM_AGE", "QLD 普通零售员工未满 13 岁；需由 HR 核验特殊许可或适用例外，排班仍可继续。", assessmentDate, "qld_law", QueenslandChildEmploymentUrl));
        var stateIsNsw = string.Equals(profile.WorkState, "NSW", StringComparison.OrdinalIgnoreCase);
        if (!stateIsQld && !stateIsNsw)
            findings.Add(Incomplete("WORK_STATE_UNKNOWN", "工作州未知，无法确定适用的未成年用工规则。"));
        if (stateIsNsw)
            findings.Add(Review("NSW_AWARD_REVIEW", "NSW 普通零售不套用 QLD 4/8/12/38 小时规则；请按教育安排、适用 Award 与公司政策核验。", category: "award"));

        // 教育出席和通勤适用于有学校日信息的未成年人；不因 NSW 或 QLD 数值规则范围而跳过。
        AddEducationAndCommuteFindings(profile, shifts, findings);
        // 是否需要/满足休息取决于 Award；此处仅提示实际连续工作已达核实阈值，绝不虚构法定例外。
        foreach (var item in shifts.Where(item => item.IsActual && item.IsTimeKnown && EffectiveMinutes(item) >= 240 && string.IsNullOrWhiteSpace(profile.AwardCode)))
            findings.Add(Review("AWARD_BREAK_REVIEW", "实际连续工作已达到 4 小时；休息安排须按适用 Award 核实，系统未推定任何例外。", item.WorkDate.Date, "award"));
        if (!stateIsQld) return new(findings);
        if (profile.IsQldSchoolAgedChild != true)
        {
            findings.Add(profile.IsQldSchoolAgedChild.HasValue
                ? Review("QLD_PARTICIPATION_REVIEW", "不属于 QLD 学龄儿童的数值工时规则范围；教育参与安排和适用 Award 仍需核验。", category: "education", sourceUrl: QueenslandChildEmploymentUrl)
                : Incomplete("QLD_SCHOOL_AGED_UNKNOWN", "未确认是否属于 QLD 学龄儿童，不能确认 QLD 工时限额。"));
            return new(findings);
        }

        if (!profile.ExternalWorkKnown) findings.Add(Incomplete("EXTERNAL_WORK_UNKNOWN", "员工的其他工作未确认，不能确认日/周累计工时。"));
        var slices = SplitAcrossLocalDates(shifts);
        foreach (var day in slices.Select(item => item.WorkDate).Distinct())
        {
            var minutes = slices.Where(item => item.WorkDate == day).Sum(item => item.Minutes);
            if (!profile.RequiredSchoolDays.TryGetValue(day, out var schoolDay))
            {
                findings.Add(Incomplete("SCHOOL_CALENDAR_MISSING", "缺少个人学校日历，不能确认 4 或 8 小时日上限。", day));
                continue;
            }
            var limit = schoolDay.IsRequired ? 240 : 480;
            if (minutes > limit)
                findings.Add(Warning(schoolDay.IsRequired ? "QLD_SCHOOL_DAY_DAILY_LIMIT" : "QLD_NON_SCHOOL_DAY_DAILY_LIMIT", "日累计工时超过 QLD 学龄儿童上限。", day, minutes, limit, "qld_law", QueenslandChildEmploymentUrl));
        }
        var targetSundays = input.TargetWeekStart.HasValue
            ? new[] { input.TargetWeekStart.Value.Date.AddDays(-(int)input.TargetWeekStart.Value.DayOfWeek) }
            : slices.Select(item => item.WorkDate.AddDays(-(int)item.WorkDate.DayOfWeek)).Distinct();
        foreach (var sunday in targetSundays)
        {
            var dates = Enumerable.Range(0, 7).Select(offset => sunday.AddDays(offset)).ToList();
            if (dates.Any(day => !profile.RequiredSchoolDays.ContainsKey(day)))
            {
                findings.Add(Incomplete("SCHOOL_WEEK_UNKNOWN", "缺少完整个人学校日历，不能确认 12 或 38 小时周上限。", sunday));
                continue;
            }
            var minutes = slices.Where(item => item.WorkDate >= sunday && item.WorkDate < sunday.AddDays(7)).Sum(item => item.Minutes);
            var schoolWeek = dates.Any(day => profile.RequiredSchoolDays[day].IsRequired);
            var limit = schoolWeek ? 720 : 2280;
            if (minutes > limit)
                findings.Add(Warning(schoolWeek ? "QLD_SCHOOL_WEEKLY_LIMIT" : "QLD_NON_SCHOOL_WEEKLY_LIMIT", "周日开始的累计工时超过 QLD 学龄儿童周上限。", sunday, minutes, limit, "qld_law", QueenslandChildEmploymentUrl));
        }
        foreach (var item in shifts.Where(item => item.IsTimeKnown))
        {
            if (TouchesNightPeriod(item.StartLocal, item.EndLocal))
                findings.Add(Warning("QLD_NIGHT_WORK", "班次落在 QLD 22:00–06:00 限制时段。", item.StartLocal.Date, category: "qld_law", sourceUrl: QueenslandChildEmploymentUrl));
        }
        // external:planned / external:actual 只代表时间来源，不代表同一外部雇主；不得据此虚构 12 小时同雇主间隔。
        foreach (var group in shifts.Where(item => item.IsTimeKnown && !string.IsNullOrWhiteSpace(item.EmployerId)
                && !item.EmployerId.StartsWith("external:planned", StringComparison.OrdinalIgnoreCase)
                && !item.EmployerId.StartsWith("external:actual", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.EmployerId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(item => item.StartLocal).ToList();
            for (var index = 1; index < ordered.Count; index++)
                if (!string.IsNullOrWhiteSpace(ordered[index].ScheduleGuid)
                    && string.Equals(ordered[index].ScheduleGuid, ordered[index - 1].ScheduleGuid, StringComparison.OrdinalIgnoreCase)) continue;
                else if (ordered[index].StartLocal - ordered[index - 1].EndLocal < TimeSpan.FromHours(12))
                    findings.Add(Warning("QLD_SAME_EMPLOYER_REST", "同一雇主相邻班次间隔少于 12 小时。", ordered[index].WorkDate.Date, category: "qld_law", sourceUrl: QueenslandChildEmploymentUrl));
        }
        return new(findings);
    }

    private static void AddEducationAndCommuteFindings(MinorEmploymentProfile profile, List<MinorEmploymentWorkItem> shifts, List<MinorEmploymentComplianceFinding> findings)
    {
        foreach (var item in shifts.Where(item => item.IsTimeKnown))
        {
            foreach (var slice in SplitAcrossLocalDates([item]))
            {
                var hasSchoolDay = profile.RequiredSchoolDays.TryGetValue(slice.WorkDate, out var schoolDay);
                var day = slice.WorkDate.Date;
                // 保留完整日期时间：跨午夜切片的 EndLocal 是次日 00:00，不能仅比较 TimeOfDay。
                var schoolStart = hasSchoolDay && schoolDay!.Start.HasValue ? day.Add(schoolDay.Start.Value) : (DateTime?)null;
                var schoolEnd = hasSchoolDay && schoolDay!.End.HasValue ? day.Add(schoolDay.End.Value) : (DateTime?)null;
                if (hasSchoolDay && schoolDay!.IsRequired && schoolStart.HasValue && schoolEnd.HasValue
                    && slice.StartLocal < schoolEnd.Value && slice.EndLocal > schoolStart.Value)
                    findings.Add(Review("SCHOOL_ATTENDANCE_CONFLICT", "班次与已申报的学校出席时间重叠。", day, "education"));
                if (hasSchoolDay && schoolDay!.IsRequired && schoolEnd.HasValue && profile.CommuteFromSchoolMinutes.HasValue
                    && slice.StartLocal >= schoolEnd.Value
                    && slice.StartLocal < schoolEnd.Value.AddMinutes(profile.CommuteFromSchoolMinutes.Value))
                    findings.Add(Review("COMMUTE_SCHOOL_CONFLICT", "放学至门店的通勤时间不足；这是安全提醒，不是法定工时扣减。", day, "company_safety"));
                if (profile.LatestWorkEndLocalTime.HasValue && slice.EndLocal > day.Add(profile.LatestWorkEndLocalTime.Value))
                    findings.Add(Review("COMMUTE_LATEST_END_CONFLICT", "班次结束晚于已申报的安全返程最晚下班时间。", day, "company_safety"));
                if (profile.HomewardMinutes.HasValue && profile.LatestTransportLocalTime.HasValue
                    && slice.EndLocal.AddMinutes(profile.HomewardMinutes.Value) > day.Add(profile.LatestTransportLocalTime.Value))
                    findings.Add(Review("COMMUTE_HOMEWARD_CONFLICT", "下班后返程时间无法赶上已申报的最晚交通安排。", day, "company_safety"));
            }
        }
    }
    private static List<MinorEmploymentWorkSlice> SplitAcrossLocalDates(IEnumerable<MinorEmploymentWorkItem> shifts)
    {
        var result = new List<MinorEmploymentWorkSlice>();
        foreach (var item in shifts)
        {
            var totalMinutes = EffectiveMinutes(item);
            var totalDuration = (item.EndLocal - item.StartLocal).TotalMinutes;
            for (var start = item.StartLocal; start < item.EndLocal; )
            {
                var end = start.Date.AddDays(1) < item.EndLocal ? start.Date.AddDays(1) : item.EndLocal;
                var portion = totalDuration <= 0 ? 0 : (int)Math.Round(totalMinutes * ((end - start).TotalMinutes / totalDuration), MidpointRounding.AwayFromZero);
                result.Add(new(item, start.Date, start, end, portion));
                start = end;
            }
        }
        return result;
    }
    private static bool TouchesNightPeriod(DateTime start, DateTime end) => start.TimeOfDay < TimeSpan.FromHours(6) || end.TimeOfDay > TimeSpan.FromHours(22) || end.Date > start.Date;
    private static bool IsAdult(MinorEmploymentProfile profile, DateTime assessmentDate)
        => profile.DateOfBirth.HasValue && profile.DateOfBirth.Value.Date.AddYears(18) <= assessmentDate.Date;
    private static int EffectiveMinutes(MinorEmploymentWorkItem item) => item.ActualWorkedMinutes ?? (int)Math.Round((item.EndLocal - item.StartLocal).TotalMinutes, MidpointRounding.AwayFromZero);
    private static void AddProfileFindings(MinorEmploymentProfile profile, List<MinorEmploymentComplianceFinding> findings)
    {
        if (!profile.DateOfBirth.HasValue) findings.Add(Incomplete("DATE_OF_BIRTH_MISSING", "缺少出生日期，无法确认年龄适用范围。"));
        if (IsIncompleteStatus(profile.ConsentStatus)) findings.Add(Incomplete("CONSENT_NOT_READY", "家长同意/签署资料未完成或已退回。"));
        if (IsIncompleteStatus(profile.ReviewStatus)) findings.Add(Incomplete("HR_REVIEW_NOT_READY", "合规档案待 HR 审核或已退回；排班仍可继续发布。"));
        if (string.IsNullOrWhiteSpace(profile.AwardCode)) findings.Add(Review("AWARD_UNKNOWN", "适用 Award 或协议未知，相关休息和最低班长规则需 HR 核验。", category: "award"));
    }
    private static bool IsIncompleteStatus(string? status) => string.IsNullOrWhiteSpace(status) || status.Trim().ToLowerInvariant() is "pending" or "returned" or "pending_hr_review" or "signed_pending_employee_submit" or "awaiting_signature" or "awaiting_guardian_signature" or "draft";
    private static MinorEmploymentComplianceFinding Warning(string rule, string message, DateTime? date = null, int? actual = null, int? limit = null, string category = "qld_law", string? sourceUrl = null) => new(rule, "warning", message, date, actual, limit, category, sourceUrl);
    private static MinorEmploymentComplianceFinding Incomplete(string rule, string message, DateTime? date = null) => new(rule, "incomplete", message, date, null, null, "incomplete", null);
    private static MinorEmploymentComplianceFinding Review(string rule, string message, DateTime? date = null, string category = "award", string? sourceUrl = null) => new(rule, "review", message, date, null, null, category, sourceUrl);
}
public sealed class MinorEmploymentComplianceInput { public MinorEmploymentProfile? Profile { get; init; } public DateTime? AssessmentDate { get; init; } public DateTime? TargetWeekStart { get; init; } public IReadOnlyList<MinorEmploymentWorkItem> WorkItems { get; init; } = Array.Empty<MinorEmploymentWorkItem>(); }
public sealed class MinorEmploymentProfile { public DateTime? DateOfBirth { get; init; } public string? WorkState { get; init; } public bool? IsQldSchoolAgedChild { get; init; } public string? ConsentStatus { get; init; } public string? ReviewStatus { get; init; } public string? AwardCode { get; init; } public bool ExternalWorkKnown { get; init; } public int? CommuteFromSchoolMinutes { get; init; } public int? HomewardMinutes { get; init; } public TimeSpan? LatestTransportLocalTime { get; init; } public TimeSpan? LatestWorkEndLocalTime { get; init; } public IReadOnlyDictionary<DateTime, MinorEmploymentSchoolDay> RequiredSchoolDays { get; init; } = new Dictionary<DateTime, MinorEmploymentSchoolDay>(); }
public sealed class MinorEmploymentSchoolDay { public bool IsRequired { get; init; } public TimeSpan? Start { get; init; } public TimeSpan? End { get; init; } }
public sealed class MinorEmploymentWorkItem { public string? EmployerId { get; init; } public string? ScheduleGuid { get; init; } public DateTime WorkDate { get; init; } public DateTime StartLocal { get; init; } public DateTime EndLocal { get; init; } public int? ActualWorkedMinutes { get; init; } public bool IsActual { get; init; } public bool IsOngoing { get; init; } public bool IsTimeKnown { get; init; } = true; }
public sealed record MinorEmploymentWorkSlice(MinorEmploymentWorkItem Item, DateTime WorkDate, DateTime StartLocal, DateTime EndLocal, int Minutes);
public sealed record MinorEmploymentComplianceFinding(string RuleId, string Severity, string Message, DateTime? WorkDate, int? ActualMinutes, int? LimitMinutes, string RuleCategory, string? SourceUrl);
public sealed class MinorEmploymentComplianceEvaluation { public MinorEmploymentComplianceEvaluation(IReadOnlyList<MinorEmploymentComplianceFinding> findings) => Findings = findings; public IReadOnlyList<MinorEmploymentComplianceFinding> Findings { get; } public bool HasFindings => Findings.Count > 0; }
