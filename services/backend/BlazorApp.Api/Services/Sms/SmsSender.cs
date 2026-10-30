using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Sms;

/// <summary>
/// <see cref="ISmsSender"/> 的实现。发送顺序（任何一步拒绝都不会调服务商，也就不会花钱）：
/// 总闸 → 手机号规范化（只收澳洲手机号）→ 正文字符检查 → 按号每小时 / 每天限流 → 全站每日预算 →
/// 写一行 Sending 记录 → 调服务商 → 把记录更新为 Sent / Failed。
/// 限流按「尝试次数」数（含失败，因为失败也可能已计费）；并发下可能多放过一两条，这里不做加锁，换来实现简单，预算熔断是兜底。
/// 日志与记录都不写手机号明文和短信正文。
/// </summary>
internal sealed class SmsSender : ISmsSender
{
    private readonly SqlSugarContext _context;
    private readonly ISmsGateway _gateway;
    private readonly SmsOptions _options;
    private readonly ILogger<SmsSender> _logger;

    public SmsSender(SqlSugarContext context, ISmsGateway gateway, IOptions<SmsOptions> options, ILogger<SmsSender> logger)
    {
        _context = context;
        _gateway = gateway;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => _options.IsAvailable;

    public async Task<ApiResponse<bool>> SendAsync(SmsRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.IsAvailable)
            return ApiResponse<bool>.Error("短信服务未开启，暂不能发送短信", SmsErrorCodes.Disabled);

        if (!AustralianPhoneNumber.TryNormalize(request.ToPhone, out var e164))
            return ApiResponse<bool>.Error("需要有效的澳洲手机号（04 开头），固定电话收不到短信", SmsErrorCodes.InvalidPhone);

        if (!SmsMessages.IsGsm7Safe(request.Body))
        {
            // 模板里混进了非 GSM-7 字符属于代码缺陷：拒发并记日志，不要悄悄按双倍价格发出去。
            _logger.LogError("短信正文含非 GSM-7 字符，已拒绝发送，用途：{Purpose}", request.Purpose);
            return ApiResponse<bool>.Error("短信内容含不支持的字符", SmsErrorCodes.BodyNotGsm7);
        }

        var now = DateTime.UtcNow;
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddHours(-24);
        var phoneHash = HashPhone(e164);
        var db = _context.Db;

        var lastHour = await db.Queryable<SmsSendLog>()
            .Where(x => x.PhoneHash == phoneHash && x.CreatedAtUtc > hourAgo)
            .CountAsync();
        var lastDay = await db.Queryable<SmsSendLog>()
            .Where(x => x.PhoneHash == phoneHash && x.CreatedAtUtc > dayAgo)
            .CountAsync();
        if (lastHour >= _options.PerPhoneHourLimit || lastDay >= _options.PerPhoneDayLimit)
        {
            _logger.LogWarning("短信被号码限流拦截，用途：{Purpose}，号码：{Phone}", request.Purpose, AustralianPhoneNumber.Mask(e164));
            return ApiResponse<bool>.Error("该手机号短信发送过于频繁，请稍后再试", SmsErrorCodes.PhoneRateLimited);
        }

        var globalDay = await db.Queryable<SmsSendLog>().Where(x => x.CreatedAtUtc > dayAgo).CountAsync();
        if (globalDay >= _options.GlobalDayLimit)
        {
            _logger.LogError("短信全站日预算已用完（{Limit} 条），拒绝发送，用途：{Purpose}", _options.GlobalDayLimit, request.Purpose);
            return ApiResponse<bool>.Error("今日短信发送额度已用完，请明天再试或联系管理员", SmsErrorCodes.DailyBudgetExceeded);
        }

        var row = new SmsSendLog
        {
            Purpose = request.Purpose,
            PhoneHash = phoneHash,
            PhoneMask = AustralianPhoneNumber.Mask(e164),
            Segments = SmsMessages.SegmentCount(request.Body),
            Status = SmsSendStatuses.Sending,
            RelatedKey = request.RelatedKey,
            CreatedAtUtc = now,
        };
        await db.Insertable(row).ExecuteCommandAsync();

        var result = await _gateway.SendAsync(e164, request.Body, cancellationToken);

        var finishedAt = DateTime.UtcNow;
        var status = result.Success ? SmsSendStatuses.Sent : SmsSendStatuses.Failed;
        await db.Updateable<SmsSendLog>()
            .SetColumns(x => new SmsSendLog
            {
                Status = status,
                ProviderMessageId = result.ProviderMessageId,
                ErrorCode = result.ErrorCode,
                FinishedAtUtc = finishedAt,
            })
            .Where(x => x.Id == row.Id)
            .ExecuteCommandAsync();

        return result.Success
            ? ApiResponse<bool>.OK(true, "短信已发送")
            : ApiResponse<bool>.Error("短信发送失败，请稍后重试", SmsErrorCodes.SendFailed);
    }

    private string HashPhone(string e164)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.LogHashKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(e164))).ToLowerInvariant();
    }
}
