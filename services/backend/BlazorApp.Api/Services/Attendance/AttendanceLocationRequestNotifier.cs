using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 位置请求通知：请求创建后给被请求的员工发一条<b>可见</b>推送 + 站内消息。
/// 注意这不是静默推送：App 已不再声明后台定位，后台取不到位置，所以不唤醒；
/// 员工点通知或自己下次进前台时，客户端检查待办请求，仍在班中才在前台取一次位置并上报。
/// 推送不保证送达，所以「进前台消费」是兜底，这里发布失败也不影响请求本身。
/// 必须在业务事务<b>提交之后</b>调用（发布器自带事务，嵌套会破坏外层回滚语义），并且<b>永远不抛异常</b>。
/// </summary>
public interface IAttendanceLocationRequestNotifier
{
    Task NotifyRequestedAsync(AttendanceLocationRequest request);
}

internal sealed class AttendanceLocationRequestNotifier(
    SqlSugarContext context,
    INotificationPublisher publisher,
    ILogger<AttendanceLocationRequestNotifier> logger
) : IAttendanceLocationRequestNotifier
{
    internal const string FallbackRequesterName = "A manager";
    internal const string Title = "Location request";

    // 姓名放进正文前的长度上限：通知正文上限 1000 字符，姓名再长也不能让发布因超长被拒。
    private const int MaxNameLength = 80;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task NotifyRequestedAsync(AttendanceLocationRequest request)
    {
        if (request == null)
        {
            return;
        }

        try
        {
            var requesterName = await ResolveRequesterNameAsync(request.RequestedByUserGuid);
            await publisher.PublishAsync(new NotificationPublishRequest
            {
                Type = AppNotificationTypes.AttendanceLocationRequest,
                Title = Title,
                Body = BuildBody(requesterName, AttendanceLocationRequestRules.DefaultValidity),
                Audience = NotificationAudience.ForUsers(request.TargetUserGuid),
                // 有时限（默认 15 分钟）：走高优先级通道，降低被系统省电策略延后的概率。
                Priority = AppNotificationPriorities.High,
                Data = BuildData(request),
                StoreCode = request.StoreCode,
                DedupeKey = AttendanceLocationRequestRules.DedupeKey(request.RequestGuid),
                // 收件箱留痕：保留时长远长于请求有效期，见 AttendanceLocationRequestRules.NotificationRetention。
                ExpiresAtUtc = request.CreatedAt.Add(AttendanceLocationRequestRules.NotificationRetention),
                CreatedBy = AttendanceLocationRequestRules.NotificationCreator,
            });
        }
        catch (Exception ex)
        {
            // 日志只带请求 Id 与门店，不带人名；推送失败不影响请求，员工下次进前台仍会收到待办。
            logger.LogWarning(
                ex,
                "位置请求通知发布失败，请求本身不受影响: RequestGuid={RequestGuid}, StoreCode={StoreCode}",
                request.RequestGuid,
                request.StoreCode);
        }
    }

    /// <summary>正文为英文（与考勤审批通知同一口径），含请求人姓名与时限，不含坐标或其他个人信息。</summary>
    internal static string BuildBody(string requesterName, TimeSpan validity)
    {
        var minutes = (int)Math.Round(validity.TotalMinutes, MidpointRounding.AwayFromZero);
        return $"{requesterName} asked you to share your current location. "
            + $"Open the app within {minutes.ToString(CultureInfo.InvariantCulture)} minutes to respond.";
    }

    /// <summary>跳转参数只放标识与失效时间，客户端凭登录态自行拉待办详情。</summary>
    internal static Dictionary<string, string> BuildData(AttendanceLocationRequest request) => new()
    {
        ["requestGuid"] = request.RequestGuid,
        ["storeCode"] = request.StoreCode,
        ["expiresAtUtc"] = DateTime.SpecifyKind(request.ExpiresAtUtc, DateTimeKind.Utc)
            .ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>请求人显示名：只取姓名，缺失用兜底词；用户名常是邮箱，不放进通知。</summary>
    private async Task<string> ResolveRequesterNameAsync(string userGuid)
    {
        var fullName = await _db.Queryable<User>()
            .Where(user => user.UserGUID == userGuid)
            .Select(user => user.FullName)
            .FirstAsync();
        var name = string.Join(' ', (fullName ?? string.Empty).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (name.Length == 0)
        {
            return FallbackRequesterName;
        }

        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }
}
