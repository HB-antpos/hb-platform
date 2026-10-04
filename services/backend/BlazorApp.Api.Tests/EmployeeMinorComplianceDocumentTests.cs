using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class EmployeeMinorComplianceDocumentTests
{
    [Fact]
    public void QldArchive_PreservesOfficialFourPages_AndAllSignedDetails()
    {
        var profile = Profile();
        var input = EmployeeMinorComplianceDocumentBuilder.FromProfile(profile, Signature(), "I consent to lawful employment and confirm the nominated backup contact.");
        var bytes = EmployeeMinorComplianceDocumentBuilder.Build(input);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.True(pdf.NumberOfPages >= 5);
        var text = string.Join("\n", pdf.GetPages().Select(x => x.Text));
        Assert.Contains("Form Number: CE1", text);
        Assert.Contains("Casey Morgan", text);
        Assert.Contains("Second Employer Pty Ltd", text);
        Assert.Contains("Signed details appendix", text);
        Assert.Contains("2030", text); // 超过官方五个假期框的第六项仍在附页。
        Assert.Contains("12:00 - 13:00", text);
        using var reader = new iTextSharp.text.pdf.PdfReader(bytes);
        Assert.Empty(reader.AcroFields.Fields);
        WritePreview("qld-ce1-signed-test.pdf", bytes);
    }

    [Fact]
    public void NswArchive_UsesCompanyPolicyAndDoesNotIncludeQldForm()
    {
        var profile = Profile();
        profile.StateCode = "NSW"; profile.FormType = "NSW_COMPANY_CONSENT";
        var bytes = EmployeeMinorComplianceDocumentBuilder.Build(EmployeeMinorComplianceDocumentBuilder.FromProfile(profile, Signature(), "Company safeguarding consent"));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var text = string.Join("\n", pdf.GetPages().Select(x => x.Text));
        Assert.Contains("NSW - Parent / guardian company consent", text);
        Assert.DoesNotContain("Form Number: CE1", text);
        Assert.Contains("Casey Morgan", text);
        WritePreview("nsw-consent-signed-test.pdf", bytes);
    }

    [Fact]
    public void Signature_RejectsBlankAndInvalidImages()
    {
        Assert.Throws<ArgumentException>(() => EmployeeMinorComplianceDocumentBuilder.DecodeSignature("Casey Morgan"));
        Assert.Throws<ArgumentException>(() => EmployeeMinorComplianceDocumentBuilder.DecodeSignature(Signature(blank: true)));
        Assert.NotEmpty(EmployeeMinorComplianceDocumentBuilder.DecodeSignature(Signature()));
    }

    internal static string Signature(bool blank = false)
    {
        using var image = new Image<Rgba32>(400, 100, Color.White);
        if (!blank)
        {
            // 人造笔迹仅用于测试图片解码与归档；不作为真实家长签名。
            for (var x = 25; x < 360; x++)
            for (var thickness = 0; thickness < 3; thickness++)
                image[x, 45 + (int)(Math.Sin(x * .1) * 20) + thickness] = Color.Black;
        }
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    internal static EmployeeMinorComplianceDto Profile() => new()
    {
        Id = 42, Version = 3, Status = "signed_pending_employee_submit", UserGUID = "test-minor", StateCode = "QLD", FormType = "QLD_CE1",
        DateOfBirth = new DateTime(2011, 5, 1), SchoolName = "Example Secondary College", YearLevel = "Year 9", CompletedYear10 = false,
        GuardianName = "Casey Morgan", GuardianSignedName = "Casey Morgan", GuardianPhone = "07 3000 0000", GuardianEmail = "casey@example.test", GuardianRelationship = "Parent",
        GuardianSignedAtUtc = new DateTime(2026, 9, 20, 3, 15, 0, DateTimeKind.Utc),
        Contacts = new() { new() { FullName = "Jordan Taylor", Phone = "0400 000 000", Email = "jordan@example.test", Address = "2 Example Street", Postcode = "4000", Relationship = "Aunt" } },
        FormData = new EmployeeMinorCe1FormDto { ChildGivenName = "Alex", ChildFamilyName = "Morgan", ChildAddress = "1 Example Street", ChildPostcode = "4000", ChildPhone = "0400 111 111", ChildEmail = "alex@example.test", GuardianAddress = "1 Example Street", GuardianPostcode = "4000", EmployerCompanyName = "Example Retail Pty Ltd", EmployerTradingName = "Example Store", EmployerAddress = "3 Example Street", EmployerPostcode = "4000", EmployerPhone = "07 3000 2222", EmployerEmail = "hr@example.test", FlexibleSchoolingQualifiedTeacher = false },
        SchoolCalendar = new EmployeeMinorSchoolCalendarDto {
            SchoolProvider = "Example Secondary College", SchoolContactName = "School Officer", SchoolContactPosition = "Year Coordinator", SchoolContactPhone = "07 3000 3333", SchoolContactEmail = "school@example.test", TimeZoneId = "Australia/Brisbane",
            WeeklySchedule = Enumerable.Range(1, 5).Select(day => new EmployeeMinorWeeklyEducationDto { DayOfWeek = (DayOfWeek)day, MustAttend = true, StartLocalTime = day == 3 ? "12:00" : "09:00", EndLocalTime = day == 3 ? "13:00" : "15:00" }).ToList(),
            Holidays = Enumerable.Range(0, 6).Select(i => new EmployeeMinorDateRangeDto { StartDate = new DateTime(2025 + i, 12, 10), EndDate = new DateTime(2025 + i, 12, 31) }).ToList(),
            TermRanges = new() { new() { StartDate = new DateTime(2026, 7, 13), EndDate = new DateTime(2026, 9, 18) } },
            PupilFreeDays = new() { new() { StartDate = new DateTime(2026, 9, 4), EndDate = new DateTime(2026, 9, 4) } } },
        OtherWork = new EmployeeMinorOtherWorkDto { HasOtherWork = true, Employers = new() {
            new() { CompanyName = "First Employer Pty Ltd", TradingName = "First Cafe", Address = "4 Example Street", Postcode = "4000", Phone = "07 3000 4444", Email = "first@example.test", WeeklyHours = new() { [DayOfWeek.Saturday] = 3 } },
            new() { CompanyName = "Second Employer Pty Ltd", TradingName = "Second Cafe", Address = "5 Example Street", Postcode = "4000", Phone = "07 3000 5555", Email = "second@example.test", WeeklyHours = new() { [DayOfWeek.Sunday] = 2 } } } },
        Commute = new EmployeeMinorCommuteDto { AfterSchoolToStoreMinutes = 25, HomewardMinutes = 30, TransportMode = "Parent pickup", PickupPerson = "Casey Morgan", LatestWorkEndLocalTime = "20:00" }
    };

    private static void WritePreview(string name, byte[] bytes)
    {
        var directory = Environment.GetEnvironmentVariable("HB_MINOR_PDF_PREVIEW_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }
}
