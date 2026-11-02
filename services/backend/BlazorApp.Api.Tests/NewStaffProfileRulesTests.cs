using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>新建员工必填规则：生日、邮箱、电话；未满 18 岁时监护人四项。</summary>
public sealed class NewStaffProfileRulesTests
{
    private static readonly DateTime Today = new(2026, 10, 30);
    private static NewStaffGuardianDto Guardian() => new() { Name = "Linh Nguyen", Relationship = "Mother", Phone = "0412 345 678", Email = "linh@example.test" };

    [Fact]
    public void 成年员工_生日邮箱电话齐全即可_不要求监护人()
    {
        var result = NewStaffProfileRules.Validate(new DateTime(1998, 7, 21), "0423 555 018", "david@example.test", null, Today);
        Assert.True(result.Ok);
        Assert.False(result.IsMinor);
    }

    [Theory]
    [InlineData(null, "0423 555 018", "a@example.test", NewStaffProfileRules.BirthdayRequiredCode)]
    [InlineData("2026-10-31", "0423 555 018", "a@example.test", NewStaffProfileRules.BirthdayInvalidCode)]
    [InlineData("1939-12-31", "0423 555 018", "a@example.test", NewStaffProfileRules.BirthdayInvalidCode)]
    [InlineData("1998-07-21", "", "a@example.test", NewStaffProfileRules.PhoneRequiredCode)]
    [InlineData("1998-07-21", "12 34", "a@example.test", NewStaffProfileRules.PhoneRequiredCode)]
    [InlineData("1998-07-21", "0423 555 018", " ", NewStaffProfileRules.EmailRequiredCode)]
    public void 缺生日电话邮箱或生日不合理_拒绝(string? birthday, string phone, string email, string code)
    {
        var result = NewStaffProfileRules.Validate(birthday is null ? null : DateTime.Parse(birthday), phone, email, null, Today);
        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void 正好18岁生日当天算成年_前一天还是未成年()
    {
        Assert.False(NewStaffProfileRules.Validate(new DateTime(2008, 10, 30), "0423 555 018", "a@example.test", null, Today).IsMinor);
        var minor = NewStaffProfileRules.Validate(new DateTime(2008, 10, 31), "0423 555 018", "a@example.test", null, Today);
        Assert.True(minor.IsMinor);
        Assert.Equal(NewStaffProfileRules.GuardianRequiredCode, minor.Code);
    }

    [Fact]
    public void 未成年_监护人四项齐全且是澳洲手机才通过()
    {
        var ok = NewStaffProfileRules.Validate(new DateTime(2011, 3, 2), "0423 111 222", "mia@example.test", Guardian(), Today);
        Assert.True(ok.Ok);
        Assert.True(ok.IsMinor);

        var noRelationship = Guardian(); noRelationship.Relationship = "";
        Assert.Equal(NewStaffProfileRules.GuardianRequiredCode, NewStaffProfileRules.Validate(new DateTime(2011, 3, 2), "0423 111 222", "mia@example.test", noRelationship, Today).Code);
        var landline = Guardian(); landline.Phone = "02 9000 1111";
        Assert.Equal(NewStaffProfileRules.GuardianPhoneInvalidCode, NewStaffProfileRules.Validate(new DateTime(2011, 3, 2), "0423 111 222", "mia@example.test", landline, Today).Code);
        var badEmail = Guardian(); badEmail.Email = "not-an-email";
        Assert.Equal(NewStaffProfileRules.GuardianEmailInvalidCode, NewStaffProfileRules.Validate(new DateTime(2011, 3, 2), "0423 111 222", "mia@example.test", badEmail, Today).Code);
    }

    [Fact]
    public void 未成年_监护人手机或邮箱不能和员工本人相同()
    {
        var samePhone = Guardian(); samePhone.Phone = "+61 423 111 222";
        Assert.Equal(NewStaffProfileRules.GuardianDuplicateCode, NewStaffProfileRules.Validate(new DateTime(2011, 3, 2), "0423 111 222", "mia@example.test", samePhone, Today).Code);
        var sameEmail = Guardian(); sameEmail.Email = "MIA@example.test";
        Assert.Equal(NewStaffProfileRules.GuardianDuplicateCode, NewStaffProfileRules.Validate(new DateTime(2011, 3, 2), "0423 111 222", "mia@example.test", sameEmail, Today).Code);
    }
}
