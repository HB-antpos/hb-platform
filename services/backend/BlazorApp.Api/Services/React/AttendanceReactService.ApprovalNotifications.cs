using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 考勤审批推送的接入点（提交 → 审批人，结果 → 申请人，失效 → 撤回审批人手里的待办）。
    /// 铁律：只能在业务事务<b>提交之后</b>调用，且这里的每个方法都<b>绝不抛异常</b>——
    /// 推送只是附加通知，失败只记日志，不能改变业务结果，也不能走到调用方「catch → Rollback → throw」的路径。
    /// 通知器为 null（未接入通知平台、旧测试夹具）时全部静默跳过。
    /// </summary>
    public partial class AttendanceReactService
    {
        /// <summary>员工提交了请假 / 补卡 / 用餐申请（事务提交后调用）；按来源回查待审记录再发给能审该门店的人。</summary>
        private async Task NotifyApprovalSubmittedAsync(string sourceType, string sourceGuid)
        {
            if (_approvalNotifier == null || !AttendanceApprovalNotificationPolicy.ShouldNotify(sourceType))
            {
                return;
            }

            try
            {
                await _approvalNotifier.NotifySubmittedAsync(sourceType, sourceGuid);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "考勤审批提交通知失败，业务结果不受影响: SourceType={SourceType}, SourceGuid={SourceGuid}",
                    sourceType,
                    sourceGuid);
            }
        }

        /// <summary>审批已通过 / 驳回（事务提交后调用，approval 为已写入终态的记录）。</summary>
        private async Task NotifyApprovalDecidedAsync(AttendanceApproval approval)
        {
            if (_approvalNotifier == null || !AttendanceApprovalNotificationPolicy.ShouldNotify(approval.SourceType))
            {
                return;
            }

            try
            {
                await _approvalNotifier.NotifyDecidedAsync(approval);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "考勤审批结果通知失败，业务结果不受影响: SourceType={SourceType}, ApprovalGuid={ApprovalGuid}",
                    approval.SourceType,
                    approval.ApprovalGuid);
            }
        }

        /// <summary>审批已失效（系统自动取消）：按审批 Id 撤回审批人手里的待办通知（事务提交后调用）。</summary>
        private async Task RevokeApprovalSubmittedNotificationsAsync(IEnumerable<string> approvalGuids)
        {
            if (_approvalNotifier == null)
            {
                return;
            }

            try
            {
                await _approvalNotifier.RevokeSubmittedAsync(approvalGuids);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "考勤审批待办通知撤回失败，业务结果不受影响");
            }
        }

        /// <summary>按来源撤回待办通知：排班取消带走的用餐申请、申请人撤销的请假等（事务提交后调用）。</summary>
        private async Task RevokeApprovalSubmittedNotificationsForSourcesAsync(
            string sourceType,
            IReadOnlyCollection<string> sourceGuids)
        {
            if (_approvalNotifier == null || sourceGuids.Count == 0)
            {
                return;
            }

            try
            {
                await _approvalNotifier.RevokeSubmittedForSourcesAsync(sourceType, sourceGuids);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "考勤审批待办通知撤回失败，业务结果不受影响: SourceType={SourceType}",
                    sourceType);
            }
        }
    }
}
