using System.ComponentModel.DataAnnotations;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services;

/// <summary>
/// 新建员工时的必填规则（Web「创建用户」、手机管理员「新增账号」、手机店长「新增员工」三个入口共用）：
/// 生日、邮箱、电话必填；生日未满 18 岁时监护人姓名、关系、手机、邮箱必填。
/// 只在这三个入口的控制器里校验，批量导入、门店账号批量创建等内部流程不受影响；编辑已有员工也不受影响。
/// </summary>
public static class NewStaffProfileRules
{
    public const string BirthdayRequiredCode = "NEW_STAFF_BIRTHDAY_REQUIRED";
    public const string BirthdayInvalidCode = "NEW_STAFF_BIRTHDAY_INVALID";
    public const string PhoneRequiredCode = "NEW_STAFF_PHONE_REQUIRED";
    public const string EmailRequiredCode = "NEW_STAFF_EMAIL_REQUIRED";
    public const string GuardianRequiredCode = "NEW_STAFF_GUARDIAN_REQUIRED";
    public const string GuardianPhoneInvalidCode = "NEW_STAFF_GUARDIAN_PHONE_INVALID";
    public const string GuardianEmailInvalidCode = "NEW_STAFF_GUARDIAN_EMAIL_INVALID";
    public const string GuardianDuplicateCode = "NEW_STAFF_GUARDIAN_DUPLICATE";
    private static readonly DateTime EarliestBirthday = new(1940, 1, 1);

    public sealed record Result(string? Error, string? Code, bool IsMinor)
    {
        public bool Ok => Error is null;
    }

    /// <param name="today">悉尼当天（调用方传入，便于测试）。</param>
    public static Result Validate(DateTime? birthday, string? phone, string? email, NewStaffGuardianDto? guardian, DateTime today)
    {
        if (birthday is null) return new("请填写生日（看不到生日一栏请先更新 App）", BirthdayRequiredCode, false);
        var day = birthday.Value.Date;
        if (day > today.Date || day < EarliestBirthday) return new("生日无效：不能晚于今天，也不能早于 1940 年", BirthdayInvalidCode, false);
        if (string.IsNullOrWhiteSpace(email)) return new("请填写邮箱", EmailRequiredCode, false);
        if (string.IsNullOrWhiteSpace(phone)) return new("请填写电话", PhoneRequiredCode, false);
        var digits = phone.Count(char.IsDigit);
        if (digits is < 8 or > 15) return new("电话格式无效", PhoneRequiredCode, false);

        var isMinor = AgeOn(day, today) < 18;
        if (!isMinor) return new(null, null, false);
        if (guardian is null || string.IsNullOrWhiteSpace(guardian.Name) || string.IsNullOrWhiteSpace(guardian.Relationship)
            || string.IsNullOrWhiteSpace(guardian.Phone) || string.IsNullOrWhiteSpace(guardian.Email))
            return new("员工未满 18 岁，必须填写监护人姓名、关系、手机和邮箱", GuardianRequiredCode, true);
        // 监护人手机要收签字短信和验证码，必须是澳洲手机号。
        if (!AustralianPhoneNumber.TryNormalize(guardian.Phone, out var guardianE164))
            return new("监护人手机必须是 04 开头的澳洲手机号", GuardianPhoneInvalidCode, true);
        if (!new EmailAddressAttribute().IsValid(guardian.Email.Trim()))
            return new("监护人邮箱格式无效", GuardianEmailInvalidCode, true);
        var samePhone = AustralianPhoneNumber.TryNormalize(phone, out var staffE164) ? staffE164 == guardianE164 : DigitsOf(phone) == DigitsOf(guardian.Phone);
        if (samePhone) return new("监护人手机不能和员工本人的电话相同", GuardianDuplicateCode, true);
        if (string.Equals(guardian.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            return new("监护人邮箱不能和员工本人的邮箱相同", GuardianDuplicateCode, true);
        return new(null, null, true);
    }

    public static DateTime SydneyToday()
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).Date; }
        catch (TimeZoneNotFoundException) { return DateTime.UtcNow.Date; }
    }

    private static int AgeOn(DateTime birthday, DateTime day)
    {
        var age = day.Year - birthday.Year;
        if (birthday.Date > day.AddYears(-age)) age--;
        return age;
    }

    private static string DigitsOf(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
