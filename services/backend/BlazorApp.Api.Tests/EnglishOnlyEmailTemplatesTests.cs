using System;
using System.Text.RegularExpressions;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Myob;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 账号密码邮件、监护人邮件、工资单邮件的主题与正文只有英文。
/// 这里直接断言各发送器的 Build* 纯函数输出（不连 SMTP），以后有人再把中文段加回去，测试会失败。
/// </summary>
public sealed class EnglishOnlyEmailTemplatesTests
{
    // 汉字、中日韩标点、全角符号。
    private static readonly Regex Cjk = new(@"[　-〿㐀-鿿＀-￯]", RegexOptions.Compiled);

    private static void AssertEnglishOnly(EmailContent content)
    {
        Assert.False(Cjk.IsMatch(content.Subject), $"主题含中文：{content.Subject}");
        Assert.False(Cjk.IsMatch(content.Text), $"纯文本正文含中文：{content.Text}");
        Assert.False(Cjk.IsMatch(content.Html), $"HTML 正文含中文：{content.Html}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 设置或重置密码验证码邮件_只有英文且保留关键信息(bool invite)
    {
        var content = AccountPasswordEmailSender.BuildPasswordCode("Alex", "alex.login", "123456", "30 minutes", invite);

        AssertEnglishOnly(content);
        Assert.Contains("123456", content.Subject);
        foreach (var body in new[] { content.Text, content.Html })
        {
            Assert.Contains("alex.login", body);
            Assert.Contains("123456", body);
            Assert.Contains("30 minutes", body);
            Assert.Contains("Do not share this code with anyone", body);
        }
    }

    [Fact]
    public void 初始密码邮件_只有英文且保留关键信息()
    {
        var content = AccountPasswordEmailSender.BuildInitialPassword("Alex", "alex.login", "Init-Pass-9");

        AssertEnglishOnly(content);
        foreach (var body in new[] { content.Text, content.Html })
        {
            Assert.Contains("alex.login", body);
            Assert.Contains("Init-Pass-9", body);
            Assert.Contains("choose your own password", body);
        }
    }

    private const string AppLink = "https://hotbargain.vip/app";

    [Fact]
    public void 新建员工的初始密码邮件_带App下载短链_纯文本与HTML都有_且仍只有英文()
    {
        var content = AccountPasswordEmailSender.BuildInitialPassword("Alex", "alex.login", "Init-Pass-9", isReset: false, appLink: AppLink);

        AssertEnglishOnly(content);
        Assert.Contains($"Get the latest HB app (iPhone and Android): {AppLink}\n", content.Text);
        Assert.Contains($"<a href=\"{AppLink}\">{AppLink}</a>", content.Html);
        // 下载入口放在「登录说明」之后、「不要分享密码」之前，密码仍在正文里。
        Assert.True(content.Text.IndexOf(AppLink, StringComparison.Ordinal) < content.Text.IndexOf("Do not share", StringComparison.Ordinal));
        Assert.Contains("Init-Pass-9", content.Text);
    }

    [Fact]
    public void 重置密码邮件_不带下载短链_员工已经装过App()
    {
        var content = AccountPasswordEmailSender.BuildInitialPassword("Alex", "alex.login", "Ab3x", isReset: true, appLink: AppLink);

        Assert.DoesNotContain(AppLink, content.Text);
        Assert.DoesNotContain(AppLink, content.Html);
    }

    [Theory]
    [InlineData(true, true)]   // 邀请设置密码 = 账号刚创建，带下载短链
    [InlineData(false, false)] // 自助找回密码，不带
    public void 验证码邮件_只有邀请文案带下载短链(bool invite, bool expectLink)
    {
        var content = AccountPasswordEmailSender.BuildPasswordCode("Alex", "alex.login", "123456", "30 minutes", invite, AppLink);

        AssertEnglishOnly(content);
        Assert.Equal(expectLink, content.Text.Contains(AppLink, StringComparison.Ordinal));
        Assert.Equal(expectLink, content.Html.Contains($"href=\"{AppLink}\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 没有下载短链时_正文不出现空的下载行(string? appLink)
    {
        var content = AccountPasswordEmailSender.BuildInitialPassword("Alex", "alex.login", "Init-Pass-9", isReset: false, appLink: appLink);

        Assert.DoesNotContain("Get the latest HB app", content.Text);
        Assert.DoesNotContain("Get the latest HB app", content.Html);
        Assert.DoesNotContain("href", content.Html);
    }

    [Fact]
    public void 重置密码邮件_只有英文_写明旧密码已失效_且不再说账号已创建()
    {
        var content = AccountPasswordEmailSender.BuildInitialPassword("Alex", "alex.login", "Ab3x", isReset: true);

        AssertEnglishOnly(content);
        Assert.Contains("has been reset", content.Subject);
        foreach (var body in new[] { content.Text, content.Html })
        {
            Assert.Contains("alex.login", body);
            Assert.Contains("Ab3x", body);
            Assert.Contains("New password", body);
            Assert.Contains("old password no longer works", body);
            Assert.Contains("choose your own password", body);
            Assert.DoesNotContain("account has been created", body);
            // 旧密码已失效，不能再劝员工「忽略」这封邮件，否则会把员工锁在外面。
            Assert.DoesNotContain("you can ignore it", body);
        }
    }

    [Fact]
    public void 绑定邮箱验证码邮件_只有英文且保留关键信息()
    {
        var content = AccountPasswordEmailSender.BuildEmailChangeCode("Alex", "alex.login", "654321", "10 minutes");

        AssertEnglishOnly(content);
        Assert.Contains("654321", content.Subject);
        foreach (var body in new[] { content.Text, content.Html })
        {
            Assert.Contains("alex.login", body);
            Assert.Contains("654321", body);
            Assert.Contains("10 minutes", body);
        }
    }

    [Fact]
    public void 监护人签署链接邮件_只有英文且保留链接与有效期()
    {
        const string url = "https://hotbargain.vip/minor-employment/sign#token=abc";
        var content = MinorGuardianEmailSender.BuildSigningLink("Pat", "Sam Lee", url, "05/11/2026 14:00");

        AssertEnglishOnly(content);
        Assert.Contains("Sam Lee", content.Subject);
        foreach (var body in new[] { content.Text, content.Html })
        {
            Assert.Contains("Sam Lee", body);
            Assert.Contains("05/11/2026 14:00", body);
            Assert.Contains("Sydney time", body);
        }

        Assert.Contains(url, content.Text);
        Assert.Contains(url, content.Html);
    }

    [Fact]
    public void 监护人验证码邮件_只有英文且强调不能告诉员工()
    {
        var content = MinorGuardianEmailSender.BuildVerificationCode("Pat", "246810", 10);

        AssertEnglishOnly(content);
        Assert.Contains("246810", content.Subject);
        foreach (var body in new[] { content.Text, content.Html })
        {
            Assert.Contains("246810", body);
            Assert.Contains("10 minutes", body);
            Assert.Contains("including the employee", body);
        }
    }

    [Theory]
    [InlineData("Jane Sample", null)]
    [InlineData("", "Please check the hours on the 14th.")]
    public void 工资单邮件_只有英文且不含金额(string employeeName, string? note)
    {
        var email = new MyobPayslipEmail(
            "jane@example.com", null, employeeName, "Hot Bargain Pty Ltd", "09/10/2026 to 22/10/2026",
            "23/10/2026", note, MyobPayslipService.EmailSubject(Record()), "Payslip.pdf", new byte[] { 1 });

        var text = MyobPayslipEmailSender.BuildText(email);
        var html = MyobPayslipEmailSender.BuildHtml(email);

        AssertEnglishOnly(new EmailContent(email.Subject, text, html));
        foreach (var body in new[] { text, html })
        {
            Assert.Contains("09/10/2026 to 22/10/2026", body);
            Assert.Contains("23/10/2026", body);
            Assert.Contains("do not forward this email", body);
        }

        if (note is not null)
        {
            Assert.Contains(note, text);
            Assert.Contains("Message from the sender:", text);
        }
    }

    private static MyobPayrollAdviceRecord Record() => new(
        EmployerName: "Hot Bargain Pty Ltd",
        EmployerAbn: null,
        EmployeeUid: "emp-1",
        EmployeeName: "Jane Sample",
        EmployeeDisplayId: null,
        PeriodStart: new DateTime(2026, 10, 9),
        PeriodEnd: new DateTime(2026, 10, 22),
        PaymentDate: new DateTime(2026, 10, 23),
        PayFrequency: null,
        GrossPay: null,
        NetPay: null,
        SuperFundName: null,
        HourlyRate: null,
        ChequeNumber: null,
        Lines: Array.Empty<MyobPayrollAdviceLine>());
}
