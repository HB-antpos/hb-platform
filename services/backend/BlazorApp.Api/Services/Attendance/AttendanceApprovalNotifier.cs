using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Attendance;

/// <summary>
/// 考勤审批推送：提交 → 审批人，结果 → 申请人，审批失效 → 撤回审批人手里的待办。
/// 所有方法都必须在业务事务<b>提交之后</b>调用（发布通知会自己开事务，嵌套在业务事务里会让外层回滚失效），
/// 并且<b>永远不抛异常</b>：推送只是附加通知，失败只记日志，不能改变业务结果或让接口报错。
/// </summary>
public interface IAttendanceApprovalNotifier
{
    /// <summary>
    /// 员工提交了申请（来源类型 + 来源 Id，如 Leave + LeaveGuid）：给能审该门店的人发待办通知。
    /// 按来源回查仍处于待审的审批记录，所以业务回滚了、或审批已被别人抢先处理时自然什么都不发。
    /// </summary>
    Task NotifySubmittedAsync(string sourceType, string sourceGuid);

    /// <summary>审批已通过 / 驳回（<paramref name="approval"/> 为已提交的终态记录）：通知申请人，并撤回审批人手里的待办。</summary>
    Task NotifyDecidedAsync(AttendanceApproval approval);

    /// <summary>审批被系统取消 / 申请人撤销：按审批 Id 撤回审批人手里的待办通知。</summary>
    Task RevokeSubmittedAsync(IEnumerable<string> approvalGuids);

    /// <summary>
    /// 按来源撤回待办通知（排班取消、申请人撤销请假等场景，调用方手里只有来源 Id）：
    /// 找出这些来源名下的审批并逐个撤回；撤回是幂等的，已处理过的审批再撤回不会有副作用。
    /// </summary>
    Task RevokeSubmittedForSourcesAsync(string sourceType, IEnumerable<string> sourceGuids);
}

internal sealed class AttendanceApprovalNotifier(
    SqlSugarContext context,
    INotificationPublisher publisher,
    IAttendanceApproverResolver approverResolver,
    ILogger<AttendanceApprovalNotifier> logger
) : IAttendanceApprovalNotifier
{
    internal const string FallbackEmployeeName = "An employee";
    internal const string NotificationCreator = "system:attendance-approval";

    // 姓名放进正文前的长度上限：通知正文上限 1000 字符，姓名再长也不能让发布因超长被拒。
    private const int MaxEmployeeNameLength = 80;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task NotifySubmittedAsync(string sourceType, string sourceGuid)
    {
        if (!AttendanceApprovalNotificationPolicy.ShouldNotify(sourceType) || string.IsNullOrWhiteSpace(sourceGuid))
        {
            return;
        }

        try
        {
            var approval = await _db.Queryable<AttendanceApproval>()
                .Where(item =>
                    !item.IsDeleted
                    && item.SourceType == sourceType
                    && item.SourceGuid == sourceGuid
                    && item.ReviewStatus == "Pending")
                .OrderByDescending(item => item.CreatedAt)
                .FirstAsync();
            if (approval == null)
            {
                // 没有待审记录：业务已回滚、已被处理或已被取消，没有可通知的待办。
                return;
            }

            var approvers = await approverResolver.ResolveApproverUserGuidsAsync(
                approval.StoreCode,
                approval.ApplicantUserGuid);
            if (approvers.Count == 0)
            {
                // 日志里只有门店与审批 Id，不带人名。
                logger.LogInformation(
                    "考勤审批没有可通知的审批人，未发布: StoreCode={StoreCode}, SourceType={SourceType}, ApprovalGuid={ApprovalGuid}",
                    approval.StoreCode,
                    approval.SourceType,
                    approval.ApprovalGuid);
                return;
            }

            var employeeName = await ResolveEmployeeNameAsync(approval.ApplicantUserGuid);
            var dateText = await ResolveDateTextAsync(approval);
            var dedupeKey = AttendanceApprovalNotificationPolicy.SubmittedDedupeKey(approval.ApprovalGuid);
            await publisher.PublishAsync(new NotificationPublishRequest
            {
                Type = AppNotificationTypes.ApprovalSubmitted,
                Title = AttendanceApprovalNotificationPolicy.SubmittedTitle(approval.SourceType),
                Body = AttendanceApprovalNotificationPolicy.SubmittedBody(approval.SourceType, employeeName, dateText),
                Audience = NotificationAudience.ForUsers([.. approvers]),
                Priority = AppNotificationPriorities.Normal,
                Data = BuildData(approval),
                StoreCode = approval.StoreCode,
                DedupeKey = dedupeKey,
                CreatedBy = NotificationCreator,
            });

            // 发布期间审批若已被处理或取消（别人抢先审了、申请人撤销了），处理方的撤回可能比这条通知落库更早而落了空，
            // 这里补一次撤回，避免失效的待办残留在审批人收件箱里。撤回是幂等的。
            var stillPending = await _db.Queryable<AttendanceApproval>().AnyAsync(item =>
                !item.IsDeleted
                && item.ApprovalGuid == approval.ApprovalGuid
                && item.ReviewStatus == "Pending");
            if (!stillPending)
            {
                await publisher.RevokeAsync(dedupeKey);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "考勤审批提交通知发布失败，业务结果不受影响: SourceType={SourceType}, SourceGuid={SourceGuid}",
                sourceType,
                sourceGuid);
        }
    }

    public async Task NotifyDecidedAsync(AttendanceApproval approval)
    {
        if (approval == null || !AttendanceApprovalNotificationPolicy.ShouldNotify(approval.SourceType))
        {
            return;
        }

        // 请求已处理：其他审批人手里的待办失效。与下面的结果通知互不依赖，各自失败各自记日志。
        await RevokeSubmittedAsync([approval.ApprovalGuid]);

        try
        {
            var approved = string.Equals(approval.ReviewStatus, "Approved", StringComparison.Ordinal);
            var rejected = string.Equals(approval.ReviewStatus, "Rejected", StringComparison.Ordinal);
            if (!approved && !rejected)
            {
                return;
            }

            // 申请人自己审自己的情况接口本就拒绝，这里再兜一层：不给自己发「你的申请已处理」。
            if (string.Equals(approval.ReviewerUserGuid, approval.ApplicantUserGuid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var dateText = await ResolveDateTextAsync(approval);
            var data = BuildData(approval);
            data["decision"] = approved ? "Approved" : "Rejected";
            await publisher.PublishAsync(new NotificationPublishRequest
            {
                Type = AppNotificationTypes.ApprovalDecided,
                Title = AttendanceApprovalNotificationPolicy.DecidedTitle(approval.SourceType, approved),
                Body = AttendanceApprovalNotificationPolicy.DecidedBody(approval.SourceType, approved, dateText),
                Audience = NotificationAudience.ForUsers(approval.ApplicantUserGuid),
                Priority = AppNotificationPriorities.Normal,
                Data = data,
                StoreCode = approval.StoreCode,
                DedupeKey = AttendanceApprovalNotificationPolicy.DecidedDedupeKey(approval.ApprovalGuid),
                CreatedBy = NotificationCreator,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "考勤审批结果通知发布失败，业务结果不受影响: SourceType={SourceType}, ApprovalGuid={ApprovalGuid}",
                approval.SourceType,
                approval.ApprovalGuid);
        }
    }

    public async Task RevokeSubmittedAsync(IEnumerable<string> approvalGuids)
    {
        try
        {
            var distinctGuids = approvalGuids
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var approvalGuid in distinctGuids)
            {
                // 逐个撤回、各自兜底：一条撤回失败不能让后面的待办一直挂在审批人收件箱里。
                try
                {
                    await publisher.RevokeAsync(AttendanceApprovalNotificationPolicy.SubmittedDedupeKey(approvalGuid));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "考勤审批待办通知撤回失败，业务结果不受影响: ApprovalGuid={ApprovalGuid}", approvalGuid);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "考勤审批待办通知撤回失败，业务结果不受影响");
        }
    }

    public async Task RevokeSubmittedForSourcesAsync(string sourceType, IEnumerable<string> sourceGuids)
    {
        if (!AttendanceApprovalNotificationPolicy.ShouldNotify(sourceType))
        {
            return;
        }

        try
        {
            var distinctSourceGuids = sourceGuids
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctSourceGuids.Count == 0)
            {
                return;
            }

            // 不限审批状态：撤回对已处理的审批是空操作（处理时已经撤回过），不会误伤结果通知。
            var approvalGuids = await _db.Queryable<AttendanceApproval>()
                .Where(item => item.SourceType == sourceType && distinctSourceGuids.Contains(item.SourceGuid))
                .Select(item => item.ApprovalGuid)
                .ToListAsync();
            await RevokeSubmittedAsync(approvalGuids);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "考勤审批待办通知撤回失败，业务结果不受影响: SourceType={SourceType}", sourceType);
        }
    }

    /// <summary>跳转参数只放标识，客户端凭登录态自行拉详情。</summary>
    private static Dictionary<string, string> BuildData(AttendanceApproval approval) => new()
    {
        ["approvalGuid"] = approval.ApprovalGuid,
        ["sourceType"] = approval.SourceType,
        ["storeCode"] = approval.StoreCode,
    };

    /// <summary>申请人显示名：只取姓名，缺失用兜底词；用户名常是邮箱，不放进通知。</summary>
    private async Task<string> ResolveEmployeeNameAsync(string userGuid)
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
            return FallbackEmployeeName;
        }

        return name.Length > MaxEmployeeNameLength ? name[..MaxEmployeeNameLength] : name;
    }

    /// <summary>
    /// 申请涉及的日期：请假为起止日期、补卡为补卡日期、用餐为考勤日期，格式 yyyy-MM-dd；
    /// 查不到来源记录时返回空，文案里省略日期（宁可简短，也不影响发布）。
    /// </summary>
    private async Task<string?> ResolveDateTextAsync(AttendanceApproval approval)
    {
        switch (approval.SourceType)
        {
            case AttendanceApprovalNotificationPolicy.SourceTypeLeave:
            {
                var leave = await _db.Queryable<AttendanceLeaveRequest>()
                    .Where(item => item.LeaveGuid == approval.SourceGuid)
                    .FirstAsync();
                if (leave == null)
                {
                    return null;
                }

                var start = FormatDate(leave.StartDate);
                var end = FormatDate(leave.EndDate);
                return start == end ? start : $"{start} to {end}";
            }
            case AttendanceApprovalNotificationPolicy.SourceTypePunchAdjustment:
            {
                var adjustment = await _db.Queryable<AttendancePunchAdjustment>()
                    .Where(item => item.AdjustmentGuid == approval.SourceGuid)
                    .FirstAsync();
                return adjustment == null ? null : FormatDate(adjustment.RequestedPunchTimeLocal);
            }
            case AttendanceApprovalNotificationPolicy.SourceTypeMealBreak:
            {
                var claim = await _db.Queryable<AttendanceMealClaim>()
                    .Where(item => item.ClaimGuid == approval.SourceGuid)
                    .FirstAsync();
                return claim == null ? null : FormatDate(claim.WorkDate);
            }
            default:
                return null;
        }
    }

    private static string FormatDate(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
