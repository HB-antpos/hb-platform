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

    /// <summary>发件方：字母 sender ID（最长 11 位，澳洲需先在服务商处登记）或 E.164 号码。</summary>
    public string From { get; set; } = string.Empty;

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
}
