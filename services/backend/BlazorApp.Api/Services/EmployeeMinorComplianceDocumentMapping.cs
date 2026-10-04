using System.Globalization;
using System.Text.Json;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services;

public static partial class EmployeeMinorComplianceDocumentBuilder
{
    /// <summary>官方表格使用原始字段名映射；所有重复项目同时写入签署附页，避免丢失第二雇主或额外假期。</summary>
    public static Input FromProfile(EmployeeMinorComplianceDto profile, string signatureData, string consentScope)
    {
        var form = Typed<EmployeeMinorCe1FormDto>(profile.FormData) ?? new();
        var school = Typed<EmployeeMinorSchoolCalendarDto>(profile.SchoolCalendar) ?? new();
        var otherWork = Typed<EmployeeMinorOtherWorkDto>(profile.OtherWork) ?? new();
        var commute = Typed<EmployeeMinorCommuteDto>(profile.Commute) ?? new();
        var backup = profile.Contacts.FirstOrDefault();
        var employer = otherWork.Employers.FirstOrDefault();
        var childName = $"{form.ChildGivenName} {form.ChildFamilyName}".Trim();
        var signedAt = profile.GuardianSignedAtUtc ?? throw new InvalidOperationException("签署时间缺失");
        var fields = new Dictionary<string, string>();
        void Put(int id, string? value) => fields[$"Text Field {id}"] = value ?? string.Empty;
        Put(1, profile.GuardianName); Put(2, ""); Put(3, profile.GuardianRelationship);
        Put(4, form.GuardianAddress); Put(14, form.GuardianPostcode); Put(13, profile.GuardianPhone); Put(10, profile.GuardianEmail);
        Put(15, backup?.FullName); Put(27, backup?.Address); Put(26, backup?.Postcode);
        Put(25, backup?.Phone); Put(23, backup?.Mobile); Put(22, backup?.Email);
        Put(35, form.ChildGivenName); Put(36, form.ChildFamilyName); Put(37, Date(profile.DateOfBirth)); Put(38, profile.YearLevel);
        Put(34, backup?.FullName); Put(33, form.ChildAddress); Put(32, form.ChildPostcode); Put(31, form.ChildPhone); Put(28, form.ChildEmail);
        Put(40, school.SchoolProvider ?? profile.SchoolName); Put(41, school.SchoolContactName); Put(42, school.SchoolContactPosition);
        Put(43, school.SchoolContactPhone); Put(44, school.SchoolContactMobile); Put(45, school.SchoolContactEmail);
        for (var day = 1; day <= 5; day++)
        {
            var entries = school.WeeklySchedule.Where(x => (int)x.DayOfWeek == day).ToList();
            Put(45 + day, entries.Count == 0 ? "Not declared" : string.Join("; ", entries.Select(x => x.MustAttend ? $"{x.StartLocalTime} - {x.EndLocalTime}" : "No required attendance")));
        }
        for (var index = 0; index < 5; index++) Put(51 + index, index < school.Holidays.Count ? Range(school.Holidays[index]) : "");
        Put(56, string.Join("; ", school.PupilFreeDays.Select(Range)));
        if (form.FlexibleSchoolingQualifiedTeacher.HasValue)
            fields[form.FlexibleSchoolingQualifiedTeacher.Value ? "Check Box 2" : "Check Box 3"] = "Yes";
        Put(62, form.EmployerCompanyName); Put(61, form.EmployerTradingName); Put(63, form.EmployerAddress); Put(67, form.EmployerPostcode);
        Put(66, form.EmployerPhone); Put(65, form.EmployerMobile); Put(64, form.EmployerEmail);
        Put(84, "Not applicable - ordinary retail"); Put(101, "Not applicable - ordinary retail");
        Put(108, otherWork.HasOtherWork ? employer?.CompanyName : "No other employment declared"); Put(107, employer?.TradingName);
        Put(106, employer?.Address); Put(105, employer?.Postcode); Put(104, employer?.Phone); Put(102, employer?.Email);
        int[] hoursFields = [74, 72, 71, 70, 69, 68, 73];
        for (var day = 0; day < 7; day++)
            Put(hoursFields[day], !otherWork.HasOtherWork ? "0" : employer?.WeeklyHours.TryGetValue((DayOfWeek)day, out var hours) == true ? hours.ToString("0.##", CultureInfo.InvariantCulture) : "Not declared");
        Put(115, profile.GuardianSignedName); Put(113, form.GuardianAddress); Put(116, form.GuardianPostcode); Put(117, childName);
        Put(118, form.EmployerCompanyName); Put(119, TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(signedAt, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).ToString("dd/MM/yyyy"));

        var sections = new List<Section>
        {
            new("Child and employment", new List<Field> {
                new("Child name", childName), new("Date of birth", Date(profile.DateOfBirth)), new("State / form", $"{profile.StateCode} / {profile.FormType}"),
                new("School year", profile.YearLevel), new("Completed Year 10", YesNo(profile.CompletedYear10)),
                new("Child address / postcode", $"{form.ChildAddress} {form.ChildPostcode}"), new("Child phone / email", $"{form.ChildPhone} / {form.ChildEmail}"),
                new("Employer legal company", form.EmployerCompanyName), new("Employer trading name", form.EmployerTradingName),
                new("Employer address / postcode", $"{form.EmployerAddress} {form.EmployerPostcode}"),
                new("Employer phone / mobile / email", $"{form.EmployerPhone} / {form.EmployerMobile} / {form.EmployerEmail}") }),
            new("Parent / guardian and nominated contact", new List<Field> {
                new("Parent full name", profile.GuardianName), new("Relationship", profile.GuardianRelationship),
                new("Parent phone", profile.GuardianPhone), new("Parent email", profile.GuardianEmail),
                new("Parent address / postcode", $"{form.GuardianAddress} {form.GuardianPostcode}"),
                new("Nominated contact full name", backup?.FullName), new("Nominated contact relationship", backup?.Relationship),
                new("Nominated contact phone / mobile", $"{backup?.Phone} / {backup?.Mobile}"), new("Nominated contact email", backup?.Email),
                new("Nominated contact address / postcode", $"{backup?.Address} {backup?.Postcode}") }),
            new("Education arrangements", new List<Field> {
                new("Education / participation status", profile.EducationStatus), new("Required to be enrolled", YesNo(profile.RequiredToBeEnrolled)),
                new("Education exemption verified", YesNo(profile.EducationExemptionVerified)), new("Participation end date", Date(profile.ParticipationEndDate)),
                new("School / education provider", school.SchoolProvider ?? profile.SchoolName), new("School timezone", school.TimeZoneId),
                new("School contact / position", $"{school.SchoolContactName} / {school.SchoolContactPosition}"),
                new("School contact phone / mobile / email", $"{school.SchoolContactPhone} / {school.SchoolContactMobile} / {school.SchoolContactEmail}"),
                new("Required weekly attendance", string.Join("\n", school.WeeklySchedule.Select(x => $"{x.DayOfWeek}: {(x.MustAttend ? $"{x.StartLocalTime} - {x.EndLocalTime}" : "No required attendance")}"))),
                new("Term dates", string.Join("\n", school.TermRanges.Select(Range))), new("School holidays", string.Join("\n", school.Holidays.Select(Range))),
                new("Pupil-free days", string.Join("\n", school.PupilFreeDays.Select(Range))),
                new("Qualified teacher required for flexible schooling", YesNo(form.FlexibleSchoolingQualifiedTeacher)) }),
            new("Other employment declaration", new List<Field> {
                new("Other employment", YesNo(otherWork.HasOtherWork)), new("Hours unknown", YesNo(otherWork.HoursUnknown)),
                new("Planned work intervals", Intervals(otherWork.PlannedIntervals)), new("Actual work intervals", Intervals(otherWork.ActualIntervals)) }),
        };
        for (var index = 0; index < otherWork.Employers.Count; index++)
        {
            var other = otherWork.Employers[index];
            sections.Add(new($"Other employer {index + 1}", new List<Field> {
                new("Company / trading name", $"{other.CompanyName} / {other.TradingName}"), new("Address / postcode", $"{other.Address} {other.Postcode}"),
                new("Phone / email", $"{other.Phone} / {other.Email}"),
                new("Hours per day", string.Join("\n", Enum.GetValues<DayOfWeek>().Select(day => $"{day}: {(other.WeeklyHours.TryGetValue(day, out var hours) ? hours.ToString("0.##", CultureInfo.InvariantCulture) : "Not declared")}"))) }));
        }
        sections.Add(new("Travel and safe return", new List<Field> {
            new("School to store", $"{commute.AfterSchoolToStoreMinutes} minutes"), new("Homeward journey", $"{commute.HomewardMinutes} minutes"),
            new("Transport mode", commute.TransportMode), new("Pickup person", commute.PickupPerson),
            new("Latest transport departure", commute.LatestTransportLocalTime), new("Latest work finish", commute.LatestWorkEndLocalTime) }));
        if (form.Fields.Count > 0) sections.Add(new("Additional declared information", form.Fields.Select(x => new Field(x.Key, x.Value)).ToList()));
        return new(profile.FormType, profile.Id, profile.Version, profile.GuardianSignedName ?? profile.GuardianName,
            signedAt, signatureData, consentScope, fields, sections);
    }

    private static T? Typed<T>(object? value) where T : class => value is T typed ? typed : value is null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static string Date(DateTime? value) => value?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string YesNo(bool? value) => value.HasValue ? value.Value ? "Yes" : "No" : "Not declared";
    private static string Range(EmployeeMinorDateRangeDto range) => $"{Date(range.StartDate)} - {Date(range.EndDate)} {range.Label}".Trim();
    private static string Intervals(IEnumerable<EmployeeMinorWorkIntervalDto> values) => string.Join("\n", values.Select(x => $"{x.StartUtc:yyyy-MM-dd HH:mm} UTC - {x.EndUtc:yyyy-MM-dd HH:mm} UTC; declared hours {x.Hours.ToString("0.##", CultureInfo.InvariantCulture)}; {x.Source}"));
}
