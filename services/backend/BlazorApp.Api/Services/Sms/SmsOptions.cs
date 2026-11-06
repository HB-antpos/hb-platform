using System.Text.RegularExpressions;

namespace BlazorApp.Api.Services.Sms;

/// <summary>
/// 短信通道配置。全部只从服务器 .env 经 docker-compose 注入（Sms__*），密钥禁止写入仓库或后台表。
/// 总闸 <see cref="Enabled"/> 默认关闭：没开或缺任何一项必填配置时，<see cref="IsAvailable"/> 为 false，
/// 发送一律被拒（SMS_DISABLED），调用方据此提示「短信服务未开启」，不影响邮件等其他功能。
/// </summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary>总闸，默认关。只有服务器 .env 显式写 SMS_ENABLED=true 并重建容器才会真正发短信。</summary>
    public bool Enabled { get; set; }

    /// <summary>ClickSend 用户名（账号邮箱）。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>ClickSend API Key（在 ClickSend 后台 API Credentials 页生成，可随时重置）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 发件方：字母 sender ID（最长 11 位，澳洲需先在服务商处登记）或 E.164 号码，必填。
    /// 请求里必须带 from：2026-11-06 生产实测，省略 from 时 ClickSend 返回 HTTP 400（BAD_REQUEST），短信全部失败。
    /// 字母发件名没登记时 ClickSend 不会拒绝，而是改用共享号发出并给账号发提醒邮件，短信照常送达；
    /// 想去掉提醒邮件，要么在 ClickSend 登记这个字母发件名，要么改用购买的专用号码（E.164）。
    /// </summary>
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// 额外可选的发件名（逗号 / 分号 / 空格分隔，如 ANTPOS）。与 <see cref="From"/> 一起构成「发送时可选」的白名单，
    /// <see cref="From"/> 永远是默认项。只接受字母发件名（3–11 位、字母开头、仅字母数字）或 E.164 号码，格式不对的项直接忽略。
    /// 每个字母发件名都要各自在 ClickSend 登记并通过 ACMA 验证，否则发出的短信可能显示 Unverified 或被拦截。
    /// </summary>
    public string Senders { get; set; } = string.Empty;

    /// <summary>
    /// 发送记录里手机号哈希用的 HMAC 密钥。澳洲手机号空间很小，不加密钥的哈希可被穷举还原，
    /// 所以记录表只存带密钥的哈希 + 打码号码，不存明文号码。
    /// </summary>
    public string LogHashKey { get; set; } = string.Empty;

    /// <summary>同一手机号每小时最多发送条数（含失败，因为失败也可能已计费）。</summary>
    public int PerPhoneHourLimit { get; set; } = 5;

    /// <summary>同一手机号每 24 小时最多发送条数。</summary>
    public int PerPhoneDayLimit { get; set; } = 10;

    /// <summary>全站每 24 小时最多发送条数：被批量触发时的预算熔断。</summary>
    public int GlobalDayLimit { get; set; } = 300;

    /// <summary>服务商接口地址，测试时可指向本地假服务。</summary>
    public string ApiBaseUrl { get; set; } = "https://rest.clicksend.com";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(From)
        && !string.IsNullOrWhiteSpace(LogHashKey);

    public bool IsAvailable => Enabled && IsConfigured;

    // 字母发件名：3–11 位、字母开头、仅字母数字（ACMA / ClickSend 的规则，不允许空格与下划线）；或 E.164 号码。
    private static readonly Regex SenderFormat = new(@"^(?:[A-Za-z][A-Za-z0-9]{2,10}|\+[1-9][0-9]{7,14})$", RegexOptions.Compiled);

    /// <summary>
    /// 发送时可选的发件名：默认项（<see cref="From"/>）在第一位，后面是 <see cref="Senders"/> 里格式合法且不重复的项。
    /// 这是服务端白名单，前端传来的发件名必须命中其中一项，否则任何登录账号都能让系统冒用任意名字发短信。
    /// </summary>
    public IReadOnlyList<string> GetSenderChoices()
    {
        var choices = new List<string>();
        if (!string.IsNullOrWhiteSpace(From)) choices.Add(From.Trim());

        var extras = (Senders ?? string.Empty).Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in extras)
        {
            if (!SenderFormat.IsMatch(item)) continue;
            if (choices.Any(existing => string.Equals(existing, item, StringComparison.OrdinalIgnoreCase))) continue;
            choices.Add(item);
        }

        return choices;
    }

    /// <summary>
    /// 把请求里的发件名解析成真正要传给服务商的值：没指定 = 默认项；指定了就必须在白名单里（忽略大小写，返回配置里的规范写法）；
    /// 不在白名单返回 null，调用方应拒绝发送。
    /// </summary>
    public string? ResolveSender(string? requested)
    {
        var choices = GetSenderChoices();
        if (choices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(requested)) return choices[0];
        return choices.FirstOrDefault(item => string.Equals(item, requested.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
