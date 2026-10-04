using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

/// <summary>
/// 监护人在签署页现场修改资料的合并规则：逐项与当前版本比对，只把真正变化的写回并记下修改前后值。
/// 监护人邮箱、出生日期、雇主信息、州与表单类型不在可改范围；其他工作的计划/实际工时区间、学校时区保留原值。
/// </summary>
internal static class EmployeeMinorGuardianAmendments
{
    public const string GuardianDetails = "guardianDetails";
    public const string ChildDetails = "childDetails";
    public const string Education = "education";
    public const string SchoolCalendar = "schoolCalendar";
    public const string OtherWork = "otherWork";
    public const string Commute = "commute";
    public const string BackupContact = "backupContact";

    private static readonly Regex TimePattern = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    public sealed record Change(string Group, string Field, object? Before, object? After);

    public sealed record Result(IReadOnlyList<string> Groups, IReadOnlyList<Change> Changes, List<EmployeeMinorComplianceContactDto>? Contacts)
    {
        public bool HasChanges => Changes.Count > 0;
    }

    public static Result Apply(
        EmployeeMinorCompliance row,
        IReadOnlyList<EmployeeMinorComplianceContactDto> currentContacts,
        EmployeeMinorComplianceGuardianAmendDto amend,
        JsonSerializerOptions json)
    {
        var changes = new List<Change>();
        var form = Deserialize<EmployeeMinorCe1FormDto>(row.FormDataJson, json) ?? new EmployeeMinorCe1FormDto();
        var formChanged = false;

        // 标量：null 表示不改；空白视为清空。
        void Text(string group, string field, string? incoming, string? current, Action<string?> set, bool isForm = false)
        {
            if (incoming is null) return;
            var next = Normalize(incoming);
            var prev = Normalize(current);
            if (string.Equals(prev, next, StringComparison.Ordinal)) return;
            changes.Add(new Change(group, field, prev, next));
            set(next);
            if (isForm) formChanged = true;
        }
        void Flag(string group, string field, bool? incoming, bool? current, Action<bool?> set, bool isForm = false)
        {
            if (incoming is null || incoming == current) return;
            changes.Add(new Change(group, field, current, incoming));
            set(incoming);
            if (isForm) formChanged = true;
        }

        Text(GuardianDetails, "guardianName", amend.GuardianName, row.GuardianName, v => row.GuardianName = v ?? string.Empty);
        Text(GuardianDetails, "guardianPhone", amend.GuardianPhone, row.GuardianPhone, v => row.GuardianPhone = v);
        Text(GuardianDetails, "guardianRelationship", amend.GuardianRelationship, row.GuardianRelationship, v => row.GuardianRelationship = v);
        Text(GuardianDetails, "guardianAddress", amend.GuardianAddress, form.GuardianAddress, v => form.GuardianAddress = v, true);
        Text(GuardianDetails, "guardianPostcode", amend.GuardianPostcode, form.GuardianPostcode, v => form.GuardianPostcode = v, true);

        Text(ChildDetails, "childGivenName", amend.ChildGivenName, form.ChildGivenName, v => form.ChildGivenName = v, true);
        Text(ChildDetails, "childFamilyName", amend.ChildFamilyName, form.ChildFamilyName, v => form.ChildFamilyName = v, true);
        Text(ChildDetails, "childAddress", amend.ChildAddress, form.ChildAddress, v => form.ChildAddress = v, true);
        Text(ChildDetails, "childPostcode", amend.ChildPostcode, form.ChildPostcode, v => form.ChildPostcode = v, true);
        Text(ChildDetails, "childPhone", amend.ChildPhone, form.ChildPhone, v => form.ChildPhone = v, true);
        Text(ChildDetails, "childEmail", amend.ChildEmail, form.ChildEmail, v => form.ChildEmail = v, true);

        Text(Education, "schoolName", amend.SchoolName, row.SchoolName, v => row.SchoolName = v);
        Text(Education, "yearLevel", amend.YearLevel, row.YearLevel, v => row.YearLevel = v);
        Flag(Education, "completedYear10", amend.CompletedYear10, row.CompletedYear10, v => row.CompletedYear10 = v);
        Flag(Education, "flexibleSchoolingQualifiedTeacher", amend.FlexibleSchoolingQualifiedTeacher, form.FlexibleSchoolingQualifiedTeacher, v => form.FlexibleSchoolingQualifiedTeacher = v, true);

        if (amend.SchoolCalendar is not null)
        {
            var prev = Deserialize<EmployeeMinorSchoolCalendarDto>(row.SchoolCalendarJson, json);
            var next = amend.SchoolCalendar;
            // 学校时区由系统按门店/学校确定，监护人不改。
            next.TimeZoneId = prev?.TimeZoneId ?? next.TimeZoneId;
            CompareObject(SchoolCalendar, "schoolCalendar", prev ?? new EmployeeMinorSchoolCalendarDto(), next, json, changes, v => row.SchoolCalendarJson = v);
        }
        if (amend.OtherWork is not null)
        {
            var prev = Deserialize<EmployeeMinorOtherWorkDto>(row.OtherWorkJson, json);
            var next = amend.OtherWork;
            // 计划/实际工时区间来自排班与申报流程，签署页只改雇主与有无其他工作。
            next.PlannedIntervals = prev?.PlannedIntervals ?? new();
            next.ActualIntervals = prev?.ActualIntervals ?? new();
            CompareObject(OtherWork, "otherWork", prev ?? new EmployeeMinorOtherWorkDto(), next, json, changes, v => row.OtherWorkJson = v);
        }
        if (amend.Commute is not null)
        {
            var prev = Deserialize<EmployeeMinorCommuteDto>(row.CommuteJson, json);
            CompareObject(Commute, "commute", prev ?? new EmployeeMinorCommuteDto(), amend.Commute, json, changes, v => row.CommuteJson = v);
        }

        List<EmployeeMinorComplianceContactDto>? contacts = null;
        if (amend.Contacts is not null)
        {
            var next = amend.Contacts.Select(NormalizeContact).ToList();
            var prev = currentContacts.Select(NormalizeContact).ToList();
            if (JsonSerializer.Serialize(prev, json) != JsonSerializer.Serialize(next, json))
            {
                changes.Add(new Change(BackupContact, "contacts", prev, next));
                contacts = next;
            }
        }

        if (formChanged) row.FormDataJson = JsonSerializer.Serialize(form, json);
        var groups = changes.Select(x => x.Group).Distinct().ToList();
        return new Result(groups, changes, contacts);
    }

    /// <summary>修改后的学校安排格式校验：需上学的日子必须有合法起止时间，日期区间起不晚于止。</summary>
    public static string? ValidateSchoolCalendar(EmployeeMinorSchoolCalendarDto? calendar)
    {
        if (calendar is null) return null;
        foreach (var day in calendar.WeeklySchedule.Where(x => x.MustAttend))
        {
            if (!TimePattern.IsMatch(day.StartLocalTime ?? string.Empty) || !TimePattern.IsMatch(day.EndLocalTime ?? string.Empty))
                return "需上学的日子必须填写 HH:mm 格式的开始和结束时间";
            if (string.CompareOrdinal(day.StartLocalTime, day.EndLocalTime) >= 0)
                return "上学开始时间必须早于结束时间";
        }
        if (calendar.Holidays.Concat(calendar.PupilFreeDays).Concat(calendar.TermRanges).Any(x => x.StartDate.Date > x.EndDate.Date))
            return "日期区间的开始日期不能晚于结束日期";
        return null;
    }

    private static void CompareObject<T>(string group, string field, T prev, T next, JsonSerializerOptions json, List<Change> changes, Action<string> set)
    {
        var before = JsonSerializer.Serialize(prev, json);
        var after = JsonSerializer.Serialize(next, json);
        if (before == after) return;
        changes.Add(new Change(group, field, prev, next));
        set(after);
    }

    private static EmployeeMinorComplianceContactDto NormalizeContact(EmployeeMinorComplianceContactDto x) => new()
    {
        FullName = Normalize(x.FullName) ?? string.Empty,
        Phone = Normalize(x.Phone) ?? string.Empty,
        Mobile = Normalize(x.Mobile),
        Email = Normalize(x.Email),
        Address = Normalize(x.Address),
        Postcode = Normalize(x.Postcode),
        Relationship = Normalize(x.Relationship),
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T? Deserialize<T>(string? value, JsonSerializerOptions json) where T : class =>
        string.IsNullOrWhiteSpace(value) || value == "null" ? null : JsonSerializer.Deserialize<T>(value, json);
}
