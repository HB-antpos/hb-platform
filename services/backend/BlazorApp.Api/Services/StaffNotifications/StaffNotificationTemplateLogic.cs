using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.Sms;

namespace BlazorApp.Api.Services.StaffNotifications;

/// <summary>模板里能用的变量。{{login_box}} 与 {{download_buttons}} 是整块内容，只能放进邮件。</summary>
internal static class StaffNotificationVariables
{
    public const string Name = "name";
    public const string Email = "email";
    public const string Store = "store";
    public const string AppLink = "app_link";
    public const string LoginBox = "login_box";
    public const string DownloadButtons = "download_buttons";

    public static readonly IReadOnlyList<string> All = new[] { Name, Email, Store, AppLink, LoginBox, DownloadButtons };
    public static readonly IReadOnlyList<string> BlockVariables = new[] { LoginBox, DownloadButtons };

    public static string Token(string variable) => "{{" + variable + "}}";
}

/// <summary>一个模板的完整内容（系统默认值或数据库里的版本合并后的结果）。</summary>
internal sealed record StaffNotificationTemplateContent(
    string Key,
    string Name,
    string Kind,
    string Description,
    bool IsEnabled,
    string EmailSubject,
    string EmailBody,
    string SmsBody,
    bool IsCustomized,
    DateTime? UpdatedAtUtc
)
{
    public bool IsSystem => Kind == StaffNotificationTemplateKinds.System;

    /// <summary>只有「账号已开通」会发放随机密码；发送它必须走邮件，且会重置密码、让旧登录失效。</summary>
    public bool IssuesPassword => Key == StaffNotificationTemplateDefaults.AccountActivatedKey;

    public bool RequiresEmail => IssuesPassword;

    /// <summary>短信正文留空 = 这个模板没有短信版本。</summary>
    public bool SupportsSms => !string.IsNullOrWhiteSpace(SmsBody);
}

internal static class StaffNotificationTemplateDefaults
{
    public const string AccountActivatedKey = "account-activated";

    /// <summary>
    /// 「账号已开通」的默认文案：员工用邮箱 + 随机密码登录，密码只走邮件，附移动端下载入口。
    /// 邮件保留 "choose your own password" 字样，与 10-30 起账号邮件只有英文的口径一致。
    /// 短信不含密码，只提醒去邮箱查收并给下载短链。
    /// </summary>
    public static StaffNotificationTemplateContent AccountActivated { get; } = new(
        AccountActivatedKey,
        "账号已开通",
        StaffNotificationTemplateKinds.System,
        "告知员工账号已开通、用邮箱 + 密码登录；系统生成随机密码，只通过邮件发送；附移动端最新下载链接。",
        true,
        "Your Hot Bargain staff app account is ready",
        """
Hi {{name}},

Your Hot Bargain staff app account is ready. Sign in with your email and the temporary password below.

{{login_box}}

This password expires in 72 hours. You will be asked to choose your own password the first time you sign in.

Get the latest HB app:
{{download_buttons}}

Do not share this password with anyone, including your manager. If you did not expect this email, please let your manager know.
""",
        "Hot Bargain: your staff app account is ready. Sign in with your email. We've emailed your password. Get the app: {{app_link}}",
        false,
        null
    );
}

/// <summary>模板保存时的校验。规则与前端的实时提示一致，以这里为准。</summary>
internal static class StaffNotificationTemplateValidator
{
    public const int NameMax = 100;
    public const int SubjectMax = 200;
    public const int EmailBodyMax = 4000;

    /// <summary>短信最多 3 条（153 * 3）。</summary>
    public const int SmsMax = 459;

    // 估算长度用的示例值长度（{{app_link}} 是 https://hotbargain.vip/app 的 26 个字符）。
    private const int SampleNameLength = 12;
    private const int SampleEmailLength = 24;
    private const int SampleStoreLength = 12;
    internal const int AppLinkLength = 26;

    // 汉字、中日韩标点、全角符号：邮件与短信只能英文。
    private static readonly Regex Cjk = new(@"[　-〿㐀-鿿＀-￯]", RegexOptions.Compiled);
    private static readonly Regex Token = new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled);

    public static bool ContainsCjk(string? text) => !string.IsNullOrEmpty(text) && Cjk.IsMatch(text);

    /// <summary>返回全部问题（中文）。空列表 = 通过。</summary>
    public static IReadOnlyList<string> Validate(
        string name,
        string emailSubject,
        string emailBody,
        string smsBody,
        bool issuesPassword
    )
    {
        var errors = new List<string>();

        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0) errors.Add("请填写模板名称");
        else if (trimmedName.Length > NameMax) errors.Add($"模板名称不能超过 {NameMax} 个字符");

        ValidateEmailSubject(emailSubject ?? string.Empty, errors);
        ValidateEmailBody(emailBody ?? string.Empty, issuesPassword, errors);
        ValidateSms(smsBody ?? string.Empty, errors);
        return errors;
    }

    private static void ValidateEmailSubject(string subject, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            errors.Add("请填写邮件主题");
            return;
        }
        if (subject.Length > SubjectMax) errors.Add($"邮件主题不能超过 {SubjectMax} 个字符");
        if (subject.Contains('\r') || subject.Contains('\n')) errors.Add("邮件主题不能换行");
        if (ContainsCjk(subject)) errors.Add("邮件主题只能是英文，不能含中文字符");
        AddTokenErrors(subject, "邮件主题", allowBlocks: false, errors);
    }

    private static void ValidateEmailBody(string body, bool issuesPassword, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            errors.Add("请填写邮件正文");
            return;
        }
        if (body.Length > EmailBodyMax) errors.Add($"邮件正文不能超过 {EmailBodyMax} 个字符");
        if (ContainsCjk(body)) errors.Add("邮件正文只能是英文，不能含中文字符");
        AddTokenErrors(body, "邮件正文", allowBlocks: true, errors);

        var hasLoginBox = body.Contains(StaffNotificationVariables.Token(StaffNotificationVariables.LoginBox), StringComparison.Ordinal);
        if (issuesPassword && !hasLoginBox)
        {
            // 发放了随机密码却没放进邮件，员工就永远收不到密码。
            errors.Add("这个模板会发放随机密码，邮件正文必须包含 {{login_box}}，否则员工收不到密码");
        }
        if (!issuesPassword && hasLoginBox)
        {
            errors.Add("只有「账号已开通」会发放密码，这个模板的邮件正文不能使用 {{login_box}}");
        }
    }

    private static void ValidateSms(string sms, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(sms)) return; // 留空 = 没有短信版本

        if (sms.Length > 500) errors.Add("短信正文太长");
        AddTokenErrors(sms, "短信正文", allowBlocks: false, errors);

        // 把变量按估算长度换成占位字符再检查字符集与长度。
        var sample = Token.Replace(sms, match => SampleFor(match.Value));
        if (!SmsMessages.IsGsm7Safe(sample))
        {
            errors.Add("短信只能使用英文字母、数字和常用标点；出现中文、弯引号、长破折号或 emoji 会让一条短信变成 70 字符、费用翻倍");
        }
        if (sample.Length > SmsMax)
        {
            errors.Add($"短信按示例姓名估算超过 {SmsMax} 个字符（最多 3 条），请缩短");
        }
    }

    private static void AddTokenErrors(string text, string label, bool allowBlocks, List<string> errors)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Token.Matches(text))
        {
            var variable = match.Value[2..^2];
            if (!StaffNotificationVariables.All.Contains(variable))
            {
                if (reported.Add(match.Value)) errors.Add($"{label}里有不认识的变量 {match.Value}");
            }
            else if (!allowBlocks && StaffNotificationVariables.BlockVariables.Contains(variable))
            {
                if (reported.Add(match.Value)) errors.Add($"{label}不能使用 {match.Value}（只有邮件正文能用）");
            }
        }

        // 去掉合法写法后还剩下单独的 {{ 或 }}，说明变量写坏了。
        var leftover = Token.Replace(text, string.Empty);
        if (leftover.Contains("{{", StringComparison.Ordinal) || leftover.Contains("}}", StringComparison.Ordinal))
        {
            errors.Add($"{label}里的变量格式不对，应写成 {{{{name}}}} 这样");
        }
    }

    private static string SampleFor(string token) => token switch
    {
        "{{name}}" => new string('x', SampleNameLength),
        "{{email}}" => new string('x', SampleEmailLength),
        "{{store}}" => new string('x', SampleStoreLength),
        "{{app_link}}" => new string('x', AppLinkLength),
        _ => string.Empty,
    };
}

/// <summary>渲染一封邮件 / 一条短信所需的变量值。</summary>
internal sealed record StaffNotificationRenderContext(
    string Name,
    string Email,
    string Store,
    string AppLink,
    string IosUrl,
    string AndroidUrl
);

/// <summary>
/// 模板渲染：纯函数，不连数据库、不发信，测试直接断言输出。
/// 邮件同时产出纯文本与 HTML：所有变量值与模板文字都做 HTML 编码；整行只写 {{login_box}} / {{download_buttons}}
/// 时渲染成有样式的块，写在句子中间则按纯文本展开。
/// </summary>
internal static class StaffNotificationRenderer
{
    private static readonly Regex Token = new(@"\{\{([a-z_]+)\}\}", RegexOptions.Compiled);
    private const string PasswordMask = "••••";

    public static EmailContent RenderEmail(
        string subjectTemplate,
        string bodyTemplate,
        StaffNotificationRenderContext context,
        string? password
    )
    {
        // 主题必须是单行：校验已拒绝带换行的模板，这里再兜一层，保证邮件头里永远不会出现换行（防头注入）。
        var subject = RenderInline(subjectTemplate, context, password, html: false, singleLine: true)
            .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var text = RenderText(bodyTemplate, context, password);
        var html = RenderHtml(bodyTemplate, context, password);
        return new EmailContent(subject, text, html);
    }

    /// <summary>短信渲染：姓名、分店名过 <see cref="SmsMessages.SafeName"/>（去变音符号，含中文就用兜底词），保证不破坏 GSM-7 编码。</summary>
    public static string RenderSms(string smsTemplate, StaffNotificationRenderContext context)
    {
        return Token.Replace(smsTemplate, match => match.Groups[1].Value switch
        {
            StaffNotificationVariables.Name => SmsMessages.SafeName(context.Name, "there"),
            StaffNotificationVariables.Store => SmsMessages.SafeName(context.Store, "your store"),
            StaffNotificationVariables.Email => SmsMessages.IsGsm7Safe(context.Email) ? context.Email : "your email",
            StaffNotificationVariables.AppLink => context.AppLink,
            _ => string.Empty, // 整块变量在短信里没有意义（校验已拒绝），防御性地去掉
        });
    }

    private static string RenderText(string bodyTemplate, StaffNotificationRenderContext context, string? password)
    {
        var normalized = bodyTemplate.Replace("\r\n", "\n");
        return RenderInline(normalized, context, password, html: false, singleLine: false);
    }

    private static string RenderHtml(string bodyTemplate, StaffNotificationRenderContext context, string? password)
    {
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.55;color:#1f2937;max-width:560px\">");
        html.Append("<div style=\"margin:0 0 14px;padding:0 0 8px;border-bottom:2px solid #1677ff;font-size:15px;font-weight:700\">Hot Bargain</div>");

        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p style=\"margin:0 0 12px\">").Append(string.Join("<br>", paragraph)).Append("</p>");
            paragraph.Clear();
        }

        foreach (var line in bodyTemplate.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == StaffNotificationVariables.Token(StaffNotificationVariables.LoginBox))
            {
                Flush();
                html.Append(LoginBoxHtml(context, password));
            }
            else if (trimmed == StaffNotificationVariables.Token(StaffNotificationVariables.DownloadButtons))
            {
                Flush();
                html.Append(DownloadButtonsHtml(context));
            }
            else if (trimmed.Length == 0)
            {
                Flush();
            }
            else
            {
                paragraph.Add(RenderInline(line, context, password, html: true, singleLine: true));
            }
        }

        Flush();
        html.Append("</div>");
        return html.ToString();
    }

    private static string RenderInline(string template, StaffNotificationRenderContext context, string? password, bool html, bool singleLine)
    {
        // 模板文字本身也要编码（管理员写的 < > & 不能原样进 HTML）；花括号不受 HtmlEncode 影响，变量仍能匹配。
        var source = html ? WebUtility.HtmlEncode(template) : template;
        return Token.Replace(source, match =>
        {
            var value = match.Groups[1].Value switch
            {
                StaffNotificationVariables.Name => context.Name,
                StaffNotificationVariables.Email => context.Email,
                StaffNotificationVariables.Store => context.Store,
                StaffNotificationVariables.AppLink => context.AppLink,
                StaffNotificationVariables.LoginBox => LoginBoxText(context, password),
                StaffNotificationVariables.DownloadButtons => DownloadButtonsText(context),
                _ => match.Value,
            };
            if (singleLine) value = value.Replace("\r", " ").Replace("\n", " ");
            return html ? WebUtility.HtmlEncode(value) : value;
        });
    }

    private static string LoginBoxText(StaffNotificationRenderContext context, string? password) =>
        $"Sign-in email: {context.Email}\nTemporary password: {password ?? string.Empty}";

    private static string DownloadButtonsText(StaffNotificationRenderContext context) =>
        $"iPhone (App Store): {context.IosUrl}\nAndroid (download): {context.AndroidUrl}";

    private static string LoginBoxHtml(StaffNotificationRenderContext context, string? password) =>
        "<div style=\"margin:0 0 14px;padding:10px 12px;border-radius:8px;background:#f5f7fb\">" +
        "<div style=\"color:#667085;font-size:12px\">Sign-in email</div>" +
        $"<div style=\"font-weight:600;margin-bottom:6px\">{WebUtility.HtmlEncode(context.Email)}</div>" +
        "<div style=\"color:#667085;font-size:12px\">Temporary password</div>" +
        $"<div style=\"font-family:Consolas,Menlo,monospace;font-size:24px;font-weight:700;letter-spacing:2px\">{WebUtility.HtmlEncode(password ?? string.Empty)}</div>" +
        "</div>";

    private static string DownloadButtonsHtml(StaffNotificationRenderContext context) =>
        "<table role=\"presentation\" style=\"margin:0 0 14px;border-collapse:collapse\"><tr>" +
        ButtonCell("iPhone (App Store)", context.IosUrl, trailingGap: true) +
        ButtonCell("Android (download)", context.AndroidUrl, trailingGap: false) +
        "</tr></table>";

    private static string ButtonCell(string label, string url, bool trailingGap) =>
        $"<td style=\"{(trailingGap ? "padding-right:8px" : "padding:0")}\">" +
        $"<a href=\"{WebUtility.HtmlEncode(url)}\" style=\"display:inline-block;padding:9px 16px;border-radius:8px;background:#1677ff;color:#ffffff;text-decoration:none;font-weight:600\">{label}</a></td>";

    /// <summary>预览用的密码占位（后台任何人都看不到真实密码）。</summary>
    public static string PreviewPassword => PasswordMask;
}
