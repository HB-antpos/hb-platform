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
