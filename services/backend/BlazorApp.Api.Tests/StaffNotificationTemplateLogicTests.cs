using System.Linq;
using System.Text.RegularExpressions;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Api.Services.StaffNotifications;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>员工通知的纯逻辑：模板校验、渲染、邮箱存疑规则。不连数据库、不发信。</summary>
public sealed class StaffNotificationTemplateLogicTests
{
    private static readonly Regex Cjk = new(@"[　-〿㐀-鿿＀-￯]", RegexOptions.Compiled);

    private static StaffNotificationRenderContext Context(string name = "Mia Chen", string store = "Sunnybank") => new(
        name,
        "mia.chen@example.com",
        store,
        "https://hotbargain.vip/app",
        "https://apps.apple.com/au/app/hbgroup/id6786073002",
        "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production"
    );

    private static System.Collections.Generic.IReadOnlyList<string> ValidateDefaults(bool issuesPassword = true)
    {
        var template = StaffNotificationTemplateDefaults.AccountActivated;
        return StaffNotificationTemplateValidator.Validate(template.Name, template.EmailSubject, template.EmailBody, template.SmsBody, issuesPassword);
    }

    // ───────────── 校验 ─────────────

    [Fact]
    public void 账号已开通默认模板_自己通过校验且只有英文()
    {
        Assert.Empty(ValidateDefaults());

        var template = StaffNotificationTemplateDefaults.AccountActivated;
        Assert.False(Cjk.IsMatch(template.EmailSubject));
        Assert.False(Cjk.IsMatch(template.EmailBody));
        Assert.False(Cjk.IsMatch(template.SmsBody));
        Assert.True(template.IssuesPassword);
        Assert.True(template.RequiresEmail);
        Assert.True(template.SupportsSms);
        Assert.Contains("choose your own password", template.EmailBody); // 与账号邮件的英文单语约定保持一致
    }

    [Fact]
    public void 默认短信_渲染后是单条_且字符全在保守GSM7子集里()
    {
        var sms = StaffNotificationRenderer.RenderSms(StaffNotificationTemplateDefaults.AccountActivated.SmsBody, Context());

        Assert.True(SmsMessages.IsGsm7Safe(sms));
        Assert.Equal(1, SmsMessages.SegmentCount(sms));
        Assert.Contains("https://hotbargain.vip/app", sms);
        // 短信不含密码：只提醒去邮箱查收。
        Assert.DoesNotContain("password:", sms, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(139, sms.Length); // 设计图写的 139 / 160，改文案时要同步设计与前端提示
    }

    [Fact]
    public void 发放密码的模板_邮件正文缺少login_box_不允许保存()
    {
        var template = StaffNotificationTemplateDefaults.AccountActivated;
        var body = template.EmailBody.Replace("{{login_box}}", "Your password is in the app.");

        var errors = StaffNotificationTemplateValidator.Validate(template.Name, template.EmailSubject, body, template.SmsBody, issuesPassword: true);

        Assert.Contains(errors, item => item.Contains("login_box"));
    }

    [Fact]
    public void 自建模板_不能使用login_box_防止没有走密码通道却声称发了密码()
    {
        var errors = StaffNotificationTemplateValidator.Validate("Holiday notice", "Roster update", "Hi {{name}},\n\n{{login_box}}", string.Empty, issuesPassword: false);

        Assert.Contains(errors, item => item.Contains("login_box"));
    }

    [Theory]
    [InlineData("Hi {{nickname}}", "不认识的变量")]
    [InlineData("Hi {{name}, welcome", "变量格式不对")]
    [InlineData("你好 {{name}}", "不能含中文")]
    public void 邮件正文_未知变量_花括号写坏_中文_都被拒绝(string body, string expected)
    {
        var errors = StaffNotificationTemplateValidator.Validate("Notice", "Subject", body, string.Empty, issuesPassword: false);

        Assert.Contains(errors, item => item.Contains(expected));
    }

    [Fact]
    public void 邮件主题_不能换行_不能含整块变量_不能含中文()
    {
        Assert.Contains(StaffNotificationTemplateValidator.Validate("Notice", "Line1\nLine2", "Body", string.Empty, false), item => item.Contains("换行"));
        Assert.Contains(StaffNotificationTemplateValidator.Validate("Notice", "Hi {{login_box}}", "Body", string.Empty, false), item => item.Contains("login_box"));
        Assert.Contains(StaffNotificationTemplateValidator.Validate("Notice", "通知", "Body", string.Empty, false), item => item.Contains("中文"));
    }

    [Theory]
    [InlineData("Don’t forget to sign in")]      // 弯引号会让整条短信变成 UCS-2（70 字符 / 条）
    [InlineData("Your roster — updated")]          // 长破折号
    [InlineData("Sign in 请登录")]
    [InlineData("Open {{download_buttons}}")]      // 整块变量不能进短信
    [InlineData("Your password is {{login_box}}")] // 密码绝不能进短信
    public void 短信正文_非GSM7字符和整块变量_被拒绝(string sms)
    {
        var errors = StaffNotificationTemplateValidator.Validate("Notice", "Subject", "Body", sms, issuesPassword: false);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void 短信正文_按示例值估算超过3条_被拒绝_留空表示没有短信版本()
    {
        var tooLong = new string('a', 460);
        Assert.Contains(StaffNotificationTemplateValidator.Validate("Notice", "Subject", "Body", tooLong, false), item => item.Contains("最多 3 条"));
        Assert.Empty(StaffNotificationTemplateValidator.Validate("Notice", "Subject", "Body", string.Empty, false));

        // {{app_link}} 按 26 个字符估算：433 个字符 + 链接正好 459。
        var edge = new string('a', 433) + "{{app_link}}";
        Assert.Empty(StaffNotificationTemplateValidator.Validate("Notice", "Subject", "Body", edge, false));
        Assert.NotEmpty(StaffNotificationTemplateValidator.Validate("Notice", "Subject", "Body", edge + "a", false));
    }

    [Fact]
    public void 模板名称_必填_不超过100字符()
    {
        Assert.Contains(StaffNotificationTemplateValidator.Validate("  ", "Subject", "Body", string.Empty, false), item => item.Contains("名称"));
        Assert.Contains(StaffNotificationTemplateValidator.Validate(new string('a', 101), "Subject", "Body", string.Empty, false), item => item.Contains("名称"));
    }

    // ───────────── 渲染 ─────────────

    [Fact]
    public void 渲染账号已开通邮件_文本与HTML都带密码_主题替换变量()
    {
        var template = StaffNotificationTemplateDefaults.AccountActivated;

        var email = StaffNotificationRenderer.RenderEmail(template.EmailSubject, template.EmailBody, Context(), "k7Hm");

        Assert.Equal("Your Hot Bargain staff app account is ready", email.Subject);
        Assert.Contains("Hi Mia Chen,", email.Text);
        Assert.Contains("Sign-in email: mia.chen@example.com", email.Text);
        Assert.Contains("Temporary password: k7Hm", email.Text);
        Assert.Contains("iPhone (App Store): https://apps.apple.com/au/app/hbgroup/id6786073002", email.Text);
        Assert.Contains("Android (download): https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production", email.Text);
        Assert.Contains(">k7Hm<", email.Html);
        Assert.Contains("href=\"https://apps.apple.com/au/app/hbgroup/id6786073002\"", email.Html);
        Assert.Contains("choose your own password", email.Html);
        Assert.False(Cjk.IsMatch(email.Subject + email.Text + email.Html));
    }

    [Fact]
    public void 预览用圆点代替密码_真实密码不会出现在预览里()
    {
        var template = StaffNotificationTemplateDefaults.AccountActivated;

        var email = StaffNotificationRenderer.RenderEmail(template.EmailSubject, template.EmailBody, Context(), StaffNotificationRenderer.PreviewPassword);

        Assert.Contains("••••", email.Html);
        Assert.DoesNotContain("k7Hm", email.Html);
    }

    [Fact]
    public void 渲染邮件_变量值与模板文字都做HTML编码_换行在主题里被去掉()
    {
        var context = Context(name: "<script>alert(1)</script> & \"Bob\"");

        var email = StaffNotificationRenderer.RenderEmail("Hi {{name}}\nboom", "Hello {{name}} <b>bold</b>", context, null);

        Assert.DoesNotContain("<script>", email.Html);
        Assert.DoesNotContain("<b>bold</b>", email.Html);
        Assert.Contains("&lt;script&gt;", email.Html);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", email.Html);
        Assert.DoesNotContain("\n", email.Subject);
        // 纯文本部分不编码，原样保留。
        Assert.Contains("<script>", email.Text);
    }

    [Fact]
    public void 渲染邮件_整行才渲染成块_夹在句子里的login_box按文本展开()
    {
        var onOwnLine = StaffNotificationRenderer.RenderEmail("S", "Intro\n\n{{login_box}}\n\nEnd", Context(), "ab12");
        var inline = StaffNotificationRenderer.RenderEmail("S", "Your details: {{login_box}}", Context(), "ab12");

        Assert.Contains("background:#f5f7fb", onOwnLine.Html);       // 有样式的登录信息块
        Assert.DoesNotContain("background:#f5f7fb", inline.Html);
        Assert.Contains("Temporary password: ab12", inline.Html);
    }

    [Fact]
    public void 渲染短信_姓名过净化_中文名退成兜底词_变音符号被去掉()
    {
        const string template = "Hi {{name}} from {{store}}: {{app_link}}";

        Assert.Equal("Hi Jose from Sunnybank: https://hotbargain.vip/app", StaffNotificationRenderer.RenderSms(template, Context("José", "Sunnybank")));
        Assert.Equal("Hi there from your store: https://hotbargain.vip/app", StaffNotificationRenderer.RenderSms(template, Context("陈小明", "阳光银行店")));
    }

    // ───────────── 邮箱存疑 ─────────────

    [Theory]
    [InlineData("olivia@123.com", true)]            // 生产实测 15 个
    [InlineData("a@11.com", true)]
    [InlineData("a@ww.com", true)]
    [InlineData("a@x.com.au", true)]                // 复合后缀：主名只有 1 个字符
    [InlineData("a@localhost", true)]               // 没有点
    [InlineData("a@qq.com", false)]                 // 短但是真实邮箱服务
    [InlineData("a@163.com", false)]
    [InlineData("a@126.com", false)]
    [InlineData("mia@gmail.com", false)]
    [InlineData("mia@hotbargain.com.au", false)]
    [InlineData("mia@sthildas.qld.edu.au", false)]
    [InlineData("mia@yahoo.com", false)]
    [InlineData("", false)]
    [InlineData("not-an-email", false)]
    public void 邮箱存疑_纯数字或过短的域名且不在常见服务白名单(string email, bool expected)
    {
        Assert.Equal(expected, StaffNotificationRules.IsSuspiciousEmail(email));
    }

    [Fact]
    public void 手机号打码_只保留最后三位()
    {
        Assert.Equal("***156", StaffNotificationRules.MaskRawPhone("0491 570 156"));
        Assert.Equal("***", StaffNotificationRules.MaskRawPhone("12"));
        Assert.Equal("***", StaffNotificationRules.MaskRawPhone(null));
    }

    [Fact]
    public void 所有变量都有占位说明_变量清单与校验口径一致()
    {
        Assert.Equal(6, StaffNotificationVariables.All.Count);
        Assert.Equal(new[] { "login_box", "download_buttons" }, StaffNotificationVariables.BlockVariables.ToArray());
    }
}
