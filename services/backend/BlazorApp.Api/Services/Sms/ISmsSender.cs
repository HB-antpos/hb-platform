using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.Sms;

/// <summary>一条要发送的短信。Body 必须是 <see cref="SmsMessages"/> 里的模板产物（只含保守 GSM-7 字符）。</summary>
/// <param name="Purpose">用途，见 <see cref="SmsPurposes"/>；进发送记录，便于按用途统计花费。</param>
/// <param name="ToPhone">收件手机号，接受 0412 345 678 / +61 412 345 678 等写法，发送前统一成 E.164。</param>
/// <param name="Body">短信正文，只有英文。</param>
/// <param name="RelatedKey">关联的业务主键（如档案 Id），只放非个人信息，进发送记录便于排查。</param>
public sealed record SmsRequest(string Purpose, string ToPhone, string Body, string? RelatedKey = null);

public static class SmsPurposes
{
    public const string GuardianSigningLink = "GuardianSigningLink";
    public const string GuardianVerificationCode = "GuardianVerificationCode";
}

public static class SmsErrorCodes
{
    public const string Disabled = "SMS_DISABLED";
    public const string InvalidPhone = "SMS_INVALID_PHONE";
    public const string BodyNotGsm7 = "SMS_BODY_NOT_GSM7";
    public const string PhoneRateLimited = "SMS_PHONE_RATE_LIMITED";
    public const string DailyBudgetExceeded = "SMS_DAILY_BUDGET_EXCEEDED";
    public const string SendFailed = "SMS_SEND_FAILED";
}

/// <summary>
/// 应用层短信发送：开关、手机号规范化、按号与全站限流、发送记录都在这一层，
/// 真正调服务商的是 <see cref="ISmsGateway"/>。调用方（监护人签字、找回密码等）只依赖本接口。
/// </summary>
public interface ISmsSender
{
    /// <summary>总闸已开且配置齐全。调用方在创建会依赖短信的业务状态前先查它，避免进入「发不出验证码」的死状态。</summary>
    bool IsAvailable { get; }

    Task<ApiResponse<bool>> SendAsync(SmsRequest request, CancellationToken cancellationToken = default);
}
